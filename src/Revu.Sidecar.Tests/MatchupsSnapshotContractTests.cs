using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Contract tests for the matchup journal's sidecar slice: the GET /api/matchups
/// snapshot (lane order, group / card recency order, game labels, the
/// "New card from last game" preview and its existing-card check), the
/// from-last-game write's idempotency, and the export filters — all over the
/// real write seam (SidecarWriteScope + MatchupsRepository).
/// </summary>
public sealed class MatchupsSnapshotContractTests
{
    private static MatchupsSnapshotBuilder Builder(SidecarWriteScope scope, MatchupsRepository matchups) =>
        new(matchups, scope.Games, scope.Config, NullLogger<MatchupsSnapshotBuilder>.Instance);

    private static string FullMap() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["ownTop"] = "Aatrox", ["ownJg"] = "Lee Sin", ["ownMid"] = "Ahri", ["ownBot"] = "Kai'Sa", ["ownSupp"] = "Nautilus",
        ["enemyTop"] = "Sett", ["enemyJg"] = "Graves", ["enemyMid"] = "Syndra", ["enemyBot"] = "Tristana", ["enemySupp"] = "Renata",
    });

    private static async Task<GameStats> SeedGameAsync(
        SidecarWriteScope scope, long gameId, string position, long timestamp, bool win = true, string champion = "Kai'Sa", string? map = null)
    {
        var game = TestGameStatsFactory.Create(gameId, champion: champion, win: win, timestamp: timestamp);
        game.Position = position;
        game.ParticipantMap = map ?? FullMap();
        await scope.Games.SaveAsync(game);
        return game;
    }

    [Fact]
    public async Task BuildAsync_EmptyJournal_NoGames_ReportsBothPlainly()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);

        var snapshot = await Builder(scope, matchups).BuildAsync();

        Assert.True(snapshot.IsEmpty);
        Assert.Equal(0, snapshot.TotalCount);
        Assert.Empty(snapshot.Lanes);
        Assert.Equal(MatchupsSnapshotBuilder.EmptyMessage, snapshot.EmptyMessage);
        Assert.False(snapshot.LastGame.Available);
        Assert.Equal(0, snapshot.LastGame.GameId);
        Assert.Equal(MatchupsSnapshotBuilder.NoGamesReason, snapshot.LastGame.UnavailableReason);
        Assert.Null(snapshot.LastGame.ExistingCardId);
    }

    [Fact]
    public async Task BuildAsync_LastGame_PrefillsTheNewestGamesPairing()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await SeedGameAsync(scope, 6001, "MIDDLE", now - 7200, win: false, champion: "Ahri");
        await SeedGameAsync(scope, 6002, "BOTTOM", now - 3600, win: true);

        var snapshot = await Builder(scope, matchups).BuildAsync();

        var last = snapshot.LastGame;
        Assert.True(last.Available);
        Assert.Equal(6002, last.GameId);
        Assert.Equal("bot", last.Lane);
        Assert.Equal("Bot", last.LaneLabel);
        Assert.Equal(new[] { "Kai'Sa", "Nautilus" }, last.AllyChamps);
        Assert.Equal(new[] { "Tristana", "Renata Glasc" }, last.EnemyChamps);
        Assert.Equal("Kai'Sa + Nautilus vs Tristana + Renata Glasc", last.MatchupTitle);
        Assert.EndsWith("· Win", last.GameLabel);
        Assert.Null(last.ExistingCardId);
        Assert.Equal("", last.UnavailableReason);
    }

    /// <summary>"Last game" is the newest RANKED / MANUAL, non-hidden game — the
    /// scope every games list uses — so a newer casual or hidden game never
    /// seeds a card.</summary>
    [Fact]
    public async Task BuildAsync_LastGame_SkipsCasualAndHiddenGames()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await SeedGameAsync(scope, 6101, "TOP", now - 3600, champion: "Aatrox");
        await SeedGameAsync(scope, 6102, "MIDDLE", now - 120, champion: "Ahri");   // will be re-queued as casual
        await SeedGameAsync(scope, 6103, "JUNGLE", now - 60, champion: "Lee Sin"); // will be hidden
        using (var conn = scope.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE games SET queue_type = 'Ranked Flex' WHERE game_id = 6102";
            await cmd.ExecuteNonQueryAsync();
        }
        await scope.Games.SetHiddenAsync(6103, hidden: true);

        var snapshot = await Builder(scope, matchups).BuildAsync();

        Assert.True(snapshot.LastGame.Available);
        Assert.Equal(6101, snapshot.LastGame.GameId);
        Assert.Equal("top", snapshot.LastGame.Lane);
        Assert.Equal("Aatrox vs Sett", snapshot.LastGame.MatchupTitle);
    }

    [Fact]
    public async Task BuildAsync_GroupsWithEqualTimestamps_LeadWithTheHighestId_LikeTheExport()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        var first = await matchups.CreateAsync("bot", ["Kai'Sa", "Nautilus"], ["Tristana", "Renata Glasc"], createdAt: 500);
        var second = await matchups.CreateAsync("bot", ["Jinx", "Lulu"], ["Draven", "Thresh"], createdAt: 500);
        var third = await matchups.CreateAsync("bot", ["Kai'Sa", "Nautilus"], ["Tristana", "Renata Glasc"], createdAt: 500);

        var snapshot = await Builder(scope, matchups).BuildAsync();
        var (markdown, _) = await Builder(scope, matchups).BuildExportAsync(null, null);

        var groups = Assert.Single(snapshot.Lanes).Groups;
        Assert.Equal(new[] { third, first }, groups[0].Cards.Select(c => c.Id).ToArray());
        Assert.Equal(second, Assert.Single(groups[1].Cards).Id);
        Assert.True(
            markdown.IndexOf("### Kai'Sa + Nautilus", StringComparison.Ordinal) < markdown.IndexOf("### Jinx + Lulu", StringComparison.Ordinal),
            markdown);
    }

    /// <summary>v3.9.2: a game whose opponents weren't recorded (recovered from
    /// the client's match history) still pre-fills the lane and the player's
    /// side; the snapshot says so and tells the page what the click will do.</summary>
    [Fact]
    public async Task BuildAsync_LastGameWithUnknownOpponents_IsPartial_WithTheManualHint()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        // No participant map, no enemy laner: nothing to pre-fill the enemy side with.
        await SeedGameAsync(scope, 6003, "MIDDLE", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, champion: "Ahri", map: "");

        var snapshot = await Builder(scope, matchups).BuildAsync();

        var last = snapshot.LastGame;
        Assert.True(last.Available);
        Assert.False(last.EnemyKnown);
        Assert.Equal("mid", last.Lane);
        Assert.Equal(new[] { "Ahri" }, last.AllyChamps);
        Assert.Empty(last.EnemyChamps);
        Assert.Equal("Ahri vs ?", last.MatchupTitle);
        // TestConfigService has no Riot session → the click opens the form.
        Assert.Equal(MatchupsSnapshotBuilder.EnemyManualHint, last.Hint);
        Assert.Equal("", last.UnavailableReason);
        Assert.EndsWith("· Win", last.GameLabel);
    }

    [Fact]
    public async Task BuildAsync_LastGameWithUnknownOpponents_SignedIn_PromisesTheLookup()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        await SeedGameAsync(scope, 6005, "BOTTOM", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, champion: "Miss Fortune", map: "");
        // A live Riot session + linked account = the proxy is usable…
        scope.Config.Current.RiotSessionToken = "tok";
        scope.Config.Current.RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        scope.Config.Current.RiotId = "sami#NA1";
        scope.Config.Current.RiotRegion = "na1";
        Assert.True(scope.Config.RiotProxyEnabled);
        // …but the lookup also needs the account's PUUID: without it the hint
        // must not promise a lookup the write route can't perform.
        scope.Config.Current.RiotPuuid = "";
        Assert.Equal(MatchupsSnapshotBuilder.EnemyManualHint, (await Builder(scope, matchups).BuildAsync()).LastGame.Hint);

        scope.Config.Current.RiotPuuid = "puuid-1";
        var snapshot = await Builder(scope, matchups).BuildAsync();

        Assert.True(snapshot.LastGame.Available);
        Assert.False(snapshot.LastGame.EnemyKnown);
        Assert.Equal(MatchupsSnapshotBuilder.EnemyLookupHint, snapshot.LastGame.Hint);
    }

    /// <summary>The click already made a card for the game: the snapshot points
    /// at it and describes THAT card — never a re-derived "X vs ?" plus a
    /// "you'll add them" hint for a game whose card is done.</summary>
    [Fact]
    public async Task BuildAsync_ExistingCard_DescribesTheCard_NotAPartialPrefill()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        var game = await SeedGameAsync(scope, 6008, "UTILITY", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, champion: "Nautilus", map: "");
        // What the partial → form path creates: the player finished the card by hand.
        var id = await matchups.CreateAsync("support", ["Kai'Sa", "Nautilus"], ["Tristana", "Renata Glasc"], gameId: game.GameId);

        var last = (await Builder(scope, matchups).BuildAsync()).LastGame;

        Assert.True(last.Available);
        Assert.Equal(id, last.ExistingCardId);
        Assert.True(last.EnemyKnown);
        Assert.Equal("", last.Hint);
        Assert.Equal("support", last.Lane);
        Assert.Equal("Kai'Sa + Nautilus vs Tristana + Renata Glasc", last.MatchupTitle);
        Assert.EndsWith("· Win", last.GameLabel);
    }

    [Fact]
    public async Task BuildAsync_LastGameWithNoLane_ExplainsWhyTheButtonIsOff_AndNamesTheGame()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        // No position, no map, no configured primary role: the lane can't be told.
        await SeedGameAsync(scope, 6006, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, win: false, champion: "Ahri", map: "");
        scope.Config.Current.PrimaryRole = "";

        var snapshot = await Builder(scope, matchups).BuildAsync();

        Assert.False(snapshot.LastGame.Available);
        Assert.Equal(MatchupsSnapshotBuilder.NoPrefillReason, snapshot.LastGame.UnavailableReason);
        Assert.Equal(6006, snapshot.LastGame.GameId);
        Assert.EndsWith("· Loss", snapshot.LastGame.GameLabel);
    }

    [Fact]
    public async Task BuildAsync_ConfiguredPrimaryRole_FillsTheLaneWhenTheRowHasNone()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        var game = await SeedGameAsync(scope, 6007, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, champion: "Kai'Sa", map: "");
        await scope.Games.UpdateEnemyLanerAsync(game.GameId, "Tristana");
        scope.Config.Current.PrimaryRole = "adc";

        var snapshot = await Builder(scope, matchups).BuildAsync();

        Assert.True(snapshot.LastGame.Available);
        Assert.True(snapshot.LastGame.EnemyKnown);
        Assert.Equal("bot", snapshot.LastGame.Lane);
        Assert.Equal("Kai'Sa vs Tristana", snapshot.LastGame.MatchupTitle);
        // The lane is the configured role, not evidence from the game: the
        // click opens the form for a check rather than filing the card blind.
        Assert.Equal(MatchupsSnapshotBuilder.LaneGuessHint, snapshot.LastGame.Hint);
    }

    [Fact]
    public async Task FromLastGame_CreatesOnce_ThenTheSnapshotPointsAtTheExistingCard()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        var game = await SeedGameAsync(scope, 6004, "JUNGLE", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, champion: "Lee Sin");

        // What POST /api/matchup/from-last-game does the first time.
        var first = await MatchupsSnapshotBuilder.ResolveLastGameAsync(scope.Games, matchups);
        Assert.NotNull(first.Game);
        Assert.NotNull(first.Prefill);
        Assert.Null(first.Existing);
        var id = await matchups.CreateAsync(first.Prefill!.Lane, first.Prefill.AllyChamps, first.Prefill.EnemyChamps, gameId: first.Game!.GameId);

        // The second call finds the card instead of proposing another.
        var second = await MatchupsSnapshotBuilder.ResolveLastGameAsync(scope.Games, matchups);
        Assert.NotNull(second.Existing);
        Assert.Equal(id, second.Existing!.Id);

        var snapshot = await Builder(scope, matchups).BuildAsync();
        Assert.True(snapshot.LastGame.Available);
        Assert.Equal(id, snapshot.LastGame.ExistingCardId);

        // The pre-filled card renders with its lane pairing, empty notes, and the game label.
        var lane = Assert.Single(snapshot.Lanes);
        Assert.Equal("jungle", lane.Lane);
        Assert.Equal(2, lane.ChampSlots);
        var card = Assert.Single(Assert.Single(lane.Groups).Cards);
        Assert.Equal(id, card.Id);
        Assert.Equal(new[] { "Lee Sin", "Ahri" }, card.AllyChamps);
        Assert.Equal(new[] { "Graves", "Syndra" }, card.EnemyChamps);
        Assert.Equal("Lee Sin + Ahri vs Graves + Syndra", card.MatchupTitle);
        Assert.False(card.HasPrior);
        Assert.False(card.HasObserved);
        Assert.True(card.HasGame);
        Assert.Equal(game.GameId, card.GameId);
        Assert.EndsWith("· Win", card.GameLabel);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", card.DateText);
    }

    [Fact]
    public async Task BuildAsync_LanesInFixedOrder_GroupsAndCardsNewestFirst()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);

        var midCard = await matchups.CreateAsync("mid", ["Ahri"], ["Syndra"], createdAt: 500);
        var botA1 = await matchups.CreateAsync("bot", ["Kai'Sa", "Nautilus"], ["Tristana", "Renata Glasc"], createdAt: 300);
        var botB = await matchups.CreateAsync("bot", ["Jinx", "Lulu"], ["Draven", "Thresh"], createdAt: 200);
        var botA2 = await matchups.CreateAsync("bot", ["Kaisa", "Nautilus"], ["Tristana", "Renata"], createdAt: 400);
        var topCard = await matchups.CreateAsync("top", ["Aatrox"], ["Sett"], createdAt: 100);

        var snapshot = await Builder(scope, matchups).BuildAsync();

        Assert.Equal(5, snapshot.TotalCount);
        Assert.False(snapshot.IsEmpty);
        // Fixed lane order (top, jungle, mid, bot, support) with empty lanes omitted —
        // NOT recency order, where mid (500) would lead.
        Assert.Equal(new[] { "top", "mid", "bot" }, snapshot.Lanes.Select(l => l.Lane).ToArray());
        Assert.Equal(new[] { "Top", "Mid", "Bot" }, snapshot.Lanes.Select(l => l.LaneLabel).ToArray());
        Assert.Equal(new[] { 1, 1, 2 }, snapshot.Lanes.Select(l => l.ChampSlots).ToArray());

        var bot = snapshot.Lanes[2];
        Assert.Equal(3, bot.CardCount);
        Assert.Equal(2, bot.Groups.Count);
        // Spelling variants share the group; the group with the newest card leads.
        var groupA = bot.Groups[0];
        Assert.Equal("Kai'Sa + Nautilus vs Tristana + Renata Glasc", groupA.Title);
        Assert.Equal("bot|kaisa+nautilus|tristana+renataglasc", groupA.Key);
        Assert.Equal(400, groupA.LatestCreatedAt);
        Assert.Equal(new[] { botA2, botA1 }, groupA.Cards.Select(c => c.Id).ToArray());
        Assert.All(groupA.Cards, c => Assert.Equal(groupA.Key, c.MatchupKey));
        Assert.Equal(botB, Assert.Single(bot.Groups[1].Cards).Id);

        Assert.Equal(midCard, Assert.Single(Assert.Single(snapshot.Lanes[1].Groups).Cards).Id);
        var top = Assert.Single(Assert.Single(snapshot.Lanes[0].Groups).Cards);
        Assert.Equal(topCard, top.Id);
        Assert.False(top.HasGame);
        Assert.Null(top.GameId);
        Assert.Equal("", top.GameLabel);
    }

    [Fact]
    public async Task BuildExportAsync_AppliesLaneAndLastFilters()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        await matchups.CreateAsync("top", ["Aatrox"], ["Sett"], prior: "Top prior", createdAt: 100);
        await matchups.CreateAsync("bot", ["Kai'Sa", "Nautilus"], ["Tristana", "Renata Glasc"], prior: "Older bot prior", createdAt: 200);
        await matchups.CreateAsync("bot", ["Jinx", "Lulu"], ["Draven", "Thresh"], prior: "Newest bot prior", createdAt: 300);
        var builder = Builder(scope, matchups);

        var (all, allCount) = await builder.BuildExportAsync(null, null);
        Assert.Equal(3, allCount);
        Assert.Contains("## Top", all);
        Assert.Contains("## Bot", all);
        Assert.Contains("### Aatrox vs Sett", all);
        Assert.Contains("#### Prior", all);
        Assert.Contains("#### Observed", all);

        var (botOnly, botCount) = await builder.BuildExportAsync("bot", null);
        Assert.Equal(2, botCount);
        Assert.DoesNotContain("## Top", botOnly);
        Assert.Contains("Older bot prior", botOnly);

        var (lastOne, lastCount) = await builder.BuildExportAsync("bot", 1);
        Assert.Equal(1, lastCount);
        Assert.Contains("Newest bot prior", lastOne);
        Assert.DoesNotContain("Older bot prior", lastOne);

        var (none, noneCount) = await builder.BuildExportAsync("jungle", null);
        Assert.Equal(0, noneCount);
        Assert.Contains("_No cards._", none);
    }
}

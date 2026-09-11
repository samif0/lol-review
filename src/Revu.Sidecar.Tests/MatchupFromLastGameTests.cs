using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;
using Revu.Sidecar;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// v3.9.2: the write half of "New card from last game" — the bounded Match-V5
/// heal a signed-in player gets for a game recovered from the client's match
/// history, and the create-outright vs open-the-form decision. Over the real
/// write seam (SidecarWriteScope + GameRepository) with a fake match client.
/// </summary>
public sealed class MatchupFromLastGameTests
{
    private const string Puuid = "self-puuid";

    private sealed class FakeMatchClient : IRiotMatchClient
    {
        public JsonElement? Match { get; set; }
        public bool Hang { get; set; }
        public List<string> Requested { get; } = [];

        public async Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
        {
            Requested.Add(matchId);
            if (Hang) await Task.Delay(Timeout.InfiniteTimeSpan, ct); // a 429 back-off sleep, in effect
            return Match;
        }

        public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(null);
    }

    private static JsonElement MatchV5(params (string Puuid, int Team, string Position, string Champ)[] rows)
    {
        var participants = string.Join(",", rows.Select(r =>
            $$$"""{"puuid":"{{{r.Puuid}}}","teamId":{{{r.Team}}},"teamPosition":"{{{r.Position}}}","championName":"{{{r.Champ}}}"}"""));
        return JsonDocument.Parse($$$"""{"info":{"participants":[{{{participants}}}]}}""").RootElement;
    }

    private static JsonElement BotLaneMatch() => MatchV5(
        (Puuid, 100, "BOTTOM", "MissFortune"), ("p2", 100, "UTILITY", "Milio"),
        ("p3", 200, "BOTTOM", "Seraphine"), ("p4", 200, "UTILITY", "Maokai"),
        ("p5", 100, "MIDDLE", "Ahri"), ("p6", 200, "MIDDLE", "Syndra"));

    private static void SignIn(SidecarWriteScope scope)
    {
        scope.Config.Current.RiotSessionToken = "tok";
        scope.Config.Current.RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        scope.Config.Current.RiotId = "sami#NA1";
        scope.Config.Current.RiotRegion = "na1";
        scope.Config.Current.RiotPuuid = Puuid;
        Assert.True(MatchupFromLastGame.CanLookUpMatches(scope.Config));
    }

    /// <summary>A bot-lane game as recovered from the client's match history: a
    /// position, maybe a heuristic map, never an enemy laner.</summary>
    private static async Task<GameStats> SeedRecoveredAsync(SidecarWriteScope scope, long gameId, string position, string map = "")
    {
        var game = TestGameStatsFactory.Create(gameId, champion: "Miss Fortune");
        game.Position = position;
        game.ParticipantMap = map;
        game.EnemyLaner = "";
        await scope.Games.SaveAsync(game);
        return (await scope.Games.GetAsync(gameId))!;
    }

    private static (EnemyLanerBackfillService Service, FakeMatchClient Client) Backfill(SidecarWriteScope scope)
    {
        var client = new FakeMatchClient();
        var service = new EnemyLanerBackfillService(scope.Games, client, scope.Config, NullLogger<EnemyLanerBackfillService>.Instance);
        return (service, client);
    }

    private static Task<(GameStats Game, MatchupPrefillResult Prefill)> HealAsync(
        SidecarWriteScope scope, GameStats game, EnemyLanerBackfillService backfill, TimeSpan? budget = null) =>
        MatchupFromLastGame.HealAsync(
            game, MatchupPrefill.FromGame(game, scope.Config.PrimaryRole)!, scope.Config, backfill, scope.Games,
            NullLogger.Instance, budget);

    [Fact]
    public async Task SignedIn_RecoveredGameWithoutOpponents_IsHealedFromMatchV5_AndCreatesOutright()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = await SeedRecoveredAsync(scope, 7001, "BOTTOM");
        var (backfill, client) = Backfill(scope);
        client.Match = BotLaneMatch();

        var (healed, prefill) = await HealAsync(scope, game, backfill);

        Assert.Equal(new[] { "NA1_7001" }, client.Requested);
        Assert.True(prefill.CanCreateOutright);
        Assert.Equal("bot", prefill.Lane);
        Assert.Equal(new[] { "Miss Fortune", "Milio" }, prefill.AllyChamps);
        Assert.Equal(new[] { "Seraphine", "Maokai" }, prefill.EnemyChamps);
        // The row itself was healed, so the review header / analytics see it too.
        Assert.Equal("Seraphine", healed.EnemyLaner);
        Assert.Equal("Seraphine", (await scope.Games.GetAsync(7001))!.EnemyLaner);
    }

    /// <summary>The user's report, healed: a support whose recovered row says
    /// the bare lane ("BOTTOM") is filed under Support once Match-V5's
    /// teamPosition puts them in the support slot.</summary>
    [Fact]
    public async Task SignedIn_RecoveredSupportWithBareBottomPosition_IsFiledUnderSupport()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = TestGameStatsFactory.Create(7002, champion: "Milio");
        game.Position = "BOTTOM";
        game.ParticipantMap = "";
        game.EnemyLaner = "";
        await scope.Games.SaveAsync(game);
        game = (await scope.Games.GetAsync(7002))!;
        var (backfill, client) = Backfill(scope);
        client.Match = MatchV5(
            ("p1", 100, "BOTTOM", "MissFortune"), (Puuid, 100, "UTILITY", "Milio"),
            ("p3", 200, "BOTTOM", "Seraphine"), ("p4", 200, "UTILITY", "Maokai"));

        var (_, prefill) = await HealAsync(scope, game, backfill);

        Assert.True(prefill.CanCreateOutright);
        Assert.Equal("support", prefill.Lane);
        Assert.Equal(new[] { "Miss Fortune", "Milio" }, prefill.AllyChamps);
        Assert.Equal(new[] { "Seraphine", "Maokai" }, prefill.EnemyChamps);
    }

    /// <summary>v3.10.1: a live game whose matchup game end ESTIMATED (champ
    /// select / role priors) already fills the card, and the bounded lookup still
    /// runs to confirm it — Riot's answer wins and the row is stamped confirmed.</summary>
    [Fact]
    public async Task SignedIn_EstimatedMatchupIsConfirmedByTheLookup()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = TestGameStatsFactory.Create(7004, champion: "Miss Fortune");
        game.Position = "BOTTOM";
        game.EnemyLaner = "Maokai"; // the role-prior estimate, wrong on purpose
        game.ParticipantMap = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownBot"] = "Miss Fortune", ["ownSupp"] = "Milio", ["enemyBot"] = "Maokai", ["enemySupp"] = "Seraphine",
        });
        game.MatchupSource = MatchupSources.Heuristic;
        await scope.Games.SaveAsync(game);
        game = (await scope.Games.GetAsync(7004))!;
        var estimate = MatchupPrefill.FromGame(game)!;
        Assert.True(estimate.CanCreateOutright);                       // the page can already show it
        Assert.True(MatchupFromLastGame.NeedsLookup(game, estimate));   // but it is still unconfirmed
        var (backfill, client) = Backfill(scope);
        client.Match = BotLaneMatch();

        var (healed, prefill) = await HealAsync(scope, game, backfill);

        Assert.Equal(new[] { "NA1_7004" }, client.Requested);
        Assert.Equal(new[] { "Seraphine", "Maokai" }, prefill.EnemyChamps);
        Assert.Equal("Seraphine", healed.EnemyLaner);
        Assert.Equal(MatchupSources.MatchV5, healed.MatchupSource);
        Assert.False(MatchupFromLastGame.NeedsLookup(healed, prefill));
    }

    /// <summary>v3.10.1: an estimate the lookup could not confirm opens the form
    /// pre-filled instead of becoming a card; once Riot confirms it, the card is
    /// created outright.</summary>
    [Fact]
    public async Task UnconfirmedEstimate_OpensTheForm_ConfirmedRowCreatesOutright()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = TestGameStatsFactory.Create(7006, champion: "Miss Fortune");
        game.Position = "BOTTOM";
        game.EnemyLaner = "Maokai";
        game.ParticipantMap = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownBot"] = "Miss Fortune", ["ownSupp"] = "Milio", ["enemyBot"] = "Maokai", ["enemySupp"] = "Seraphine",
        });
        game.MatchupSource = MatchupSources.ChampSelect;
        await scope.Games.SaveAsync(game);
        game = (await scope.Games.GetAsync(7006))!;
        var (backfill, client) = Backfill(scope);
        client.Match = null; // Match-V5 has not published the game yet

        var (after, prefill) = await HealAsync(scope, game, backfill);

        Assert.Single(client.Requested);
        Assert.True(prefill.CanCreateOutright);                                 // the pre-fill is whole
        Assert.False(MatchupFromLastGame.ShouldCreateOutright(after, prefill));  // but unconfirmed: open the form
        Assert.False(prefill.LaneIsGuess);                                      // and not called a lane guess

        client.Match = BotLaneMatch();
        var (confirmed, confirmedPrefill) = await HealAsync(scope, after, backfill);

        Assert.Equal(MatchupSources.MatchV5, confirmed.MatchupSource);
        Assert.True(MatchupFromLastGame.ShouldCreateOutright(confirmed, confirmedPrefill));
    }

    /// <summary>A confirmed row (live roster / EOG / Match-V5) is not looked up again.</summary>
    [Fact]
    public async Task SignedIn_ConfirmedMatchupIsNotLookedUp()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = TestGameStatsFactory.Create(7005, champion: "Miss Fortune");
        game.Position = "BOTTOM";
        game.EnemyLaner = "Seraphine";
        game.ParticipantMap = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownBot"] = "Miss Fortune", ["ownSupp"] = "Milio", ["enemyBot"] = "Seraphine", ["enemySupp"] = "Maokai",
        });
        game.MatchupSource = MatchupSources.Live;
        await scope.Games.SaveAsync(game);
        game = (await scope.Games.GetAsync(7005))!;
        var (backfill, client) = Backfill(scope);
        client.Match = BotLaneMatch();

        var (_, prefill) = await HealAsync(scope, game, backfill);

        Assert.Empty(client.Requested);
        Assert.True(prefill.CanCreateOutright);
    }

    /// <summary>A recovered game whose heuristic map already names the opponents
    /// is still looked up (its enemy_laner is blank = never confirmed), and the
    /// authoritative answer replaces the heuristic one.</summary>
    [Fact]
    public async Task SignedIn_HeuristicMapIsReplacedByTheLookup()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var heuristic = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownBot"] = "Miss Fortune", ["ownSupp"] = "Milio", ["enemyBot"] = "Maokai", ["enemySupp"] = "Seraphine",
        });
        var game = await SeedRecoveredAsync(scope, 7003, "BOTTOM", heuristic);
        Assert.True(MatchupPrefill.FromGame(game)!.IsComplete);
        var (backfill, client) = Backfill(scope);
        client.Match = BotLaneMatch();

        var (_, prefill) = await HealAsync(scope, game, backfill);

        Assert.Single(client.Requested);
        Assert.Equal(new[] { "Seraphine", "Maokai" }, prefill.EnemyChamps);
    }

    [Fact]
    public async Task ConfirmedGame_IsNeverLookedUp()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = TestGameStatsFactory.Create(7004, champion: "Ahri");
        game.Position = "MIDDLE";
        game.EnemyLaner = "Syndra"; // written by the EOG capture: confirmed
        await scope.Games.SaveAsync(game);
        game = (await scope.Games.GetAsync(7004))!;
        var (backfill, client) = Backfill(scope);

        var (_, prefill) = await HealAsync(scope, game, backfill);

        Assert.Empty(client.Requested);
        Assert.True(prefill.CanCreateOutright);
    }

    [Fact]
    public async Task NotSignedIn_DegradesToTheFormWithoutALookup()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await SeedRecoveredAsync(scope, 7005, "BOTTOM");
        var (backfill, client) = Backfill(scope);
        client.Match = BotLaneMatch();

        var (_, prefill) = await HealAsync(scope, game, backfill);

        Assert.Empty(client.Requested);
        Assert.False(prefill.CanCreateOutright);
        Assert.Equal("bot", prefill.Lane);
        Assert.Equal(new[] { "Miss Fortune", "" }, prefill.AllySlots);
        Assert.Equal(new[] { "", "" }, prefill.EnemySlots);
    }

    [Fact]
    public async Task LookupFailure_DegradesToTheForm()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = await SeedRecoveredAsync(scope, 7006, "BOTTOM");
        var (backfill, client) = Backfill(scope);
        client.Match = null;

        var (_, prefill) = await HealAsync(scope, game, backfill);

        Assert.Single(client.Requested);
        Assert.False(prefill.IsComplete);
        Assert.Equal("", (await scope.Games.GetAsync(7006))!.EnemyLaner);
    }

    /// <summary>The proxy is sleeping through a 429 (or the network is gone):
    /// the lookup must not outlive the page's 30 s request. Past the budget the
    /// route falls back to the form instead of failing the click.</summary>
    [Fact]
    public async Task LookupPastTheBudget_DegradesToTheForm_QuicklyAndSilently()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = await SeedRecoveredAsync(scope, 7007, "BOTTOM");
        var (backfill, client) = Backfill(scope);
        client.Hang = true;

        var clock = Stopwatch.StartNew();
        var (_, prefill) = await HealAsync(scope, game, backfill, budget: TimeSpan.FromMilliseconds(200));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Single(client.Requested);
        Assert.False(prefill.IsComplete);
        Assert.Equal("bot", prefill.Lane);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        SignIn(scope);
        var game = await SeedRecoveredAsync(scope, 7008, "BOTTOM");
        var (backfill, client) = Backfill(scope);
        client.Hang = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MatchupFromLastGame.HealAsync(
            game, MatchupPrefill.FromGame(game)!, scope.Config, backfill, scope.Games, NullLogger.Instance,
            TimeSpan.FromSeconds(30), cts.Token));
    }

    /// <summary>A lane taken from the configured primary role is complete but
    /// not created outright — the form opens for a check.</summary>
    [Fact]
    public async Task GuessedLane_GoesThroughTheForm()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        scope.Config.Current.PrimaryRole = "supp";
        var game = TestGameStatsFactory.Create(7009, champion: "Milio");
        game.Position = "";
        game.ParticipantMap = "";
        game.EnemyLaner = "Maokai";
        await scope.Games.SaveAsync(game);
        game = (await scope.Games.GetAsync(7009))!;
        var (backfill, _) = Backfill(scope);

        var (_, prefill) = await HealAsync(scope, game, backfill);

        Assert.True(prefill.IsComplete);
        Assert.True(prefill.LaneIsGuess);
        Assert.False(prefill.CanCreateOutright);
        Assert.Equal("support", prefill.Lane);
        Assert.Equal(new[] { "", "Milio" }, prefill.AllySlots);
        Assert.Equal(new[] { "", "Maokai" }, prefill.EnemySlots);
    }
}

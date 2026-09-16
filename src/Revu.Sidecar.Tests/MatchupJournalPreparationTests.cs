using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class MatchupJournalPreparationTests
{
    private sealed class NoMatchClient : IRiotMatchClient
    {
        public Task<System.Text.Json.JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.JsonElement?>(null);
        public Task<System.Text.Json.JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.JsonElement?>(null);
    }

    private static async Task<GameStats> SeedAsync(SidecarWriteScope scope, long id, long timestamp = 1_780_000_000,
        string source = MatchupSources.MatchV5, bool opponentKnown = true)
    {
        var game = TestGameStatsFactory.Create(id, champion: "Ahri", timestamp: timestamp);
        game.Position = "MIDDLE";
        game.EnemyLaner = opponentKnown ? "Syndra" : "";
        game.ParticipantMap = opponentKnown ? """{"ownMid":"Ahri","enemyMid":"Syndra"}""" : "";
        game.MatchupSource = source;
        await scope.Games.SaveAsync(game);
        return game;
    }

    private static Task<MatchupPreparationResult> SelectAsync(SidecarWriteScope scope, MatchupsRepository matchups, long gameId) =>
        MatchupJournalPreparation.FromGameAsync(gameId, scope.Games, matchups, scope.Config,
            new EnemyLanerBackfillService(scope.Games, new NoMatchClient(), scope.Config, NullLogger<EnemyLanerBackfillService>.Instance),
            NullLogger.Instance);

    [Fact]
    public async Task RecentMatches_AreBoundedNewestFirst_WithExistingNotes_AndReadsCreateNothing()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        for (var i = 1; i <= 32; i++) await SeedAsync(scope, 8000 + i, 1_780_000_000 + i);
        var cardId = await matchups.CreateAsync("mid", ["Ahri"], ["Syndra"], observed: "Wait for the stun.", gameId: 8030);

        var snapshot = await new MatchupsSnapshotBuilder(matchups, scope.Games, scope.Config,
            NullLogger<MatchupsSnapshotBuilder>.Instance).BuildAsync();

        Assert.Equal(30, snapshot.RecentGames.Count);
        Assert.Equal(Enumerable.Range(8003, 30).Reverse().Select(id => (long)id), snapshot.RecentGames.Select(g => g.GameId));
        Assert.Equal(snapshot.RecentGames[0], snapshot.LastGame);
        Assert.Equal(cardId, snapshot.RecentGames.Single(g => g.GameId == 8030).ExistingCardId);
        Assert.Single(await matchups.GetAllAsync());
    }

    [Fact]
    public async Task SelectingAnOlderMatch_UsesThatMatch_AndReopeningPreservesWrittenNotes()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        await SeedAsync(scope, 8101);
        await SeedAsync(scope, 8102, 1_780_001_000);

        var created = await SelectAsync(scope, matchups, 8101);
        Assert.True(created.Created);
        Assert.Equal(8101, (await matchups.GetAsync(created.Id!.Value))!.GameId);
        await matchups.UpdateNotesAsync(created.Id.Value, "Hold charm.", "Punish the missed stun.");
        var reopened = await SelectAsync(scope, matchups, 8101);

        Assert.False(reopened.Created);
        Assert.Equal(created.Id, reopened.Id);
        var card = Assert.Single(await matchups.GetAllAsync());
        Assert.Equal("Hold charm.", card.Prior);
        Assert.Equal("Punish the missed stun.", card.Observed);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("casual")]
    [InlineData("missing")]
    public async Task SelectionAndAutoPreparation_RejectGamesOutsideVisibleHistory(string reason)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        scope.Config.Current.AutoMatchupNotesEnabled = true;
        if (reason != "missing") await SeedAsync(scope, 8201);
        if (reason == "hidden") await scope.Games.SetHiddenAsync(8201, true);
        if (reason == "casual")
        {
            using var conn = scope.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE games SET queue_type = 'Normal Draft' WHERE game_id = 8201";
            await cmd.ExecuteNonQueryAsync();
        }

        var selected = await SelectAsync(scope, matchups, 8201);
        var automatic = await MatchupJournalPreparation.TryAutoPrepareAsync(8201, scope.Games, matchups, scope.Config);

        Assert.Equal(MatchupJournalPreparation.UnavailableGameReason, selected.Error);
        Assert.Null(automatic);
        Assert.Empty(await matchups.GetAllAsync());
    }

    [Fact]
    public async Task AutoPreparation_DefaultsOff_ThenCreatesOnlyABlankCard_WhenEnabled()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        await SeedAsync(scope, 8301);

        Assert.False(scope.Config.Current.AutoMatchupNotesEnabled);
        Assert.Null(await MatchupJournalPreparation.TryAutoPrepareAsync(8301, scope.Games, matchups, scope.Config));
        Assert.Empty(await matchups.GetAllAsync());
        scope.Config.Current.AutoMatchupNotesEnabled = true;
        var prepared = await MatchupJournalPreparation.TryAutoPrepareAsync(8301, scope.Games, matchups, scope.Config);

        Assert.True(prepared!.Created);
        var card = Assert.Single(await matchups.GetAllAsync());
        Assert.Equal(8301, card.GameId);
        Assert.Equal("mid", card.Lane);
        Assert.Equal(new[] { "Ahri" }, card.AllyChamps);
        Assert.Equal(new[] { "Syndra" }, card.EnemyChamps);
        Assert.Empty(card.Prior);
        Assert.Empty(card.Observed);
    }

    [Fact]
    public async Task EstimatedMatch_WaitsForConfirmation_ThenPreparesOnce_AndRetainsUserNotes()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        await SeedAsync(scope, 8401, source: MatchupSources.Heuristic);
        scope.Config.Current.AutoMatchupNotesEnabled = true;

        Assert.Null(await MatchupJournalPreparation.TryAutoPrepareAsync(8401, scope.Games, matchups, scope.Config));
        var partial = await SelectAsync(scope, matchups, 8401);
        Assert.Null(partial.Error);
        Assert.Null(partial.Id);
        Assert.NotNull(partial.Prefill);
        await scope.Games.UpdateMatchupAsync(8401, "Lux", """{"ownMid":"Ahri","enemyMid":"Lux"}""", "MIDDLE", MatchupSources.MatchV5);

        var prepared = await MatchupJournalPreparation.TryAutoPrepareAsync(8401, scope.Games, matchups, scope.Config);
        Assert.True(prepared!.Created);
        await matchups.UpdateNotesAsync(prepared.Id!.Value, null, "Trade after her Q misses.");
        var repeated = await MatchupJournalPreparation.TryAutoPrepareAsync(8401, scope.Games, matchups, scope.Config);

        Assert.False(repeated!.Created);
        var card = Assert.Single(await matchups.GetAllAsync());
        Assert.Equal(new[] { "Lux" }, card.EnemyChamps);
        Assert.Equal("Trade after her Q misses.", card.Observed);
    }

    [Fact]
    public async Task MissingOpponent_DoesNotCreateAutomaticCard_SelectionReturnsPartialForm()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        await SeedAsync(scope, 8451, opponentKnown: false);
        scope.Config.Current.AutoMatchupNotesEnabled = true;

        Assert.Null(await MatchupJournalPreparation.TryAutoPrepareAsync(8451, scope.Games, matchups, scope.Config));
        var partial = await SelectAsync(scope, matchups, 8451);
        Assert.Null(partial.Error);
        Assert.Null(partial.Id);
        Assert.False(partial.Prefill!.IsComplete);
        Assert.Empty(await matchups.GetAllAsync());
    }

    [Fact]
    public async Task ManualAndAutomaticPreparation_RacingForOneGame_CreateOneCard_AndKeepTypedText()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        await SeedAsync(scope, 8501);
        scope.Config.Current.AutoMatchupNotesEnabled = true;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            await Task.Yield();
            if (i % 2 == 0)
                await MatchupJournalPreparation.TryAutoPrepareAsync(8501, scope.Games, matchups, scope.Config);
            else
                await MatchupJournalPreparation.CreateLinkedAsync(8501, scope.Games, matchups, "mid", ["Ahri"], ["Syndra"],
                    observed: "Keep space from the stun.", fromManualForm: true);
        }));

        var card = Assert.Single(await matchups.GetAllAsync());
        Assert.Equal("Keep space from the stun.", card.Observed);
        await Assert.ThrowsAsync<ArgumentException>(() => MatchupJournalPreparation.CreateLinkedAsync(
            8501, scope.Games, matchups, "mid", ["Ahri"], ["Syndra"], observed: "Don't replace my note.", fromManualForm: true));
        await Assert.ThrowsAsync<ArgumentException>(() => MatchupJournalPreparation.CreateLinkedAsync(
            8501, scope.Games, matchups, "mid", ["Ahri"], ["Lux"], observed: "Keep space from the stun.", fromManualForm: true));
        Assert.Equal("Keep space from the stun.", (await matchups.GetAsync(card.Id))!.Observed);
    }

    [Fact]
    public async Task RecentMatchLabels_DistinguishSameDaySameChampionGames()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        await SeedAsync(scope, 8601, 1_780_000_000);
        await SeedAsync(scope, 8602, 1_780_003_600);

        var snapshot = await new MatchupsSnapshotBuilder(matchups, scope.Games, scope.Config,
            NullLogger<MatchupsSnapshotBuilder>.Instance).BuildAsync();

        Assert.Equal(2, snapshot.RecentGames.Count);
        Assert.NotEqual(snapshot.RecentGames[0].GameLabel, snapshot.RecentGames[1].GameLabel);
        Assert.All(snapshot.RecentGames, game => Assert.EndsWith("· Win", game.GameLabel));
    }
}

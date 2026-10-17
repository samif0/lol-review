using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class RecentGameRecoveryTests
{
    private static readonly DateTime Start = new(2026, 9, 11, 17, 25, 0, DateTimeKind.Local);

    [Fact]
    public async Task RecoversTrustedRankedHistoryWithoutReimportingExistingDismissedOrCasualGames()
    {
        using var f = await Fixture.CreateAsync();
        await f.Database.SeedGameAsync(1001, champion: "Lux");
        await f.Decisions.MarkDismissedAsync([1002]);
        f.Lcu.Matches = [Match(1001), Match(1002), Match(1003, queue: 450), Match(1004, duration: 120), Match(1005)];

        var result = await f.Service.RecoverAsync();

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(1, f.Backups);
        Assert.Equal("Lux", (await f.Database.Games.GetAsync(1001))!.ChampionName);
        Assert.Null(await f.Database.Games.GetAsync(1002));
        Assert.Null(await f.Database.Games.GetAsync(1003));
        Assert.Null(await f.Database.Games.GetAsync(1004));
        Assert.Equal("Ahri", (await f.Database.Games.GetAsync(1005))!.ChampionName);
        Assert.Equal(5, (await f.Database.SessionLog.GetEntryAsync(1005))!.MentalRating);
        Assert.Equal(1005, Assert.Single(f.Events, e => e.Type == "gamesRecovered").Payload.GetProperty("gameIds")[0].GetInt64());

        var repeated = await f.Service.RecoverAsync();
        Assert.True(repeated.Ok);
        Assert.Equal(0, repeated.Recovered);
        Assert.Single(f.Events, e => e.Type == "gamesRecovered");
    }

    [Fact]
    public async Task GameSavedDuringBackupIsNotRecoveredOrRelogged()
    {
        using var f = await Fixture.CreateAsync();
        f.Lcu.Matches = [Match(1001)];
        f.BeforeBackup = async () =>
        {
            await f.Database.SeedGameAsync(1001, champion: "Lux");
            await f.Database.SessionLog.LogGameAsync(1001, "Lux", true, mentalRating: 8, preGameMood: 3);
        };
        var result = await f.Service.RecoverAsync();
        Assert.True(result.Ok);
        Assert.Equal(0, result.Recovered);
        Assert.Equal("Lux", (await f.Database.Games.GetAsync(1001))!.ChampionName);
        Assert.Equal(8, (await f.Database.SessionLog.GetEntryAsync(1001))!.MentalRating);
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task AFailedSaveStopsRecoveryWithoutClaimingSuccess_AndAnnouncesTheGamesItSaved()
    {
        using var f = await Fixture.CreateAsync();
        f.Lcu.Matches = [Match(1001), Match(1002)];
        var saved = new List<long>();
        f.SaveGame = (candidate, _) =>
        {
            // The coordinator throws when a save fails; that game then waits on Home.
            if (candidate.GameId == 1002) throw new InvalidOperationException("Game 1002 could not be saved");
            saved.Add(candidate.GameId);
            return Task.FromResult(RecoveredGameSave.Saved);
        };

        var result = await f.Service.RecoverAsync();

        Assert.False(result.Ok);
        Assert.Equal(1, result.Recovered);
        Assert.Contains("Couldn't finish recovery", result.Message);
        Assert.Equal([1001L], saved);
        Assert.Equal(1001, Assert.Single(f.Events, e => e.Type == "gamesRecovered").Payload.GetProperty("gameIds")[0].GetInt64());
    }

    [Fact]
    public async Task AGameTheCoordinatorLeftToAnotherSave_IsNotReportedAsRecovered()
    {
        using var f = await Fixture.CreateAsync();
        f.Lcu.Matches = [Match(1001)];
        f.SaveGame = (_, _) => Task.FromResult(RecoveredGameSave.NotSaved); // the monitor's own save takes it

        var result = await f.Service.RecoverAsync();

        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, result.Recovered);
        Assert.Contains("No matches were recovered", result.Message);
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task WhileTheGameJustPlayedIsStillBeingSaved_RecoverySaysToTryAgain()
    {
        using var f = await Fixture.CreateAsync();
        f.Lcu.Matches = [Match(1001), Match(1002)];
        // The capture of the game just played runs with an unknown id: no game can be taken yet.
        f.SaveGame = (candidate, _) => Task.FromResult(
            candidate.GameId == 1001 ? RecoveredGameSave.Saved : RecoveredGameSave.Busy);

        var result = await f.Service.RecoverAsync();

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.Recovered);
        Assert.StartsWith("Recovered 1 ranked game. Revu is still saving the game you just played. Try again in a minute.", result.Message);

        f.SaveGame = (_, _) => Task.FromResult(RecoveredGameSave.Busy);
        result = await f.Service.RecoverAsync();

        Assert.Equal(0, result.Recovered);
        Assert.StartsWith("Revu is still saving the game you just played. Try again in a minute.", result.Message);
        Assert.DoesNotContain("No matches were recovered", result.Message);
    }

    [Fact]
    public async Task DisconnectedClientReturnsActionableResultWithoutStartingHistoryBackupOrWrites()
    {
        using var f = await Fixture.CreateAsync();
        f.Lcu.Connected = false;
        f.Lcu.Matches = [Match(1001)];
        var result = await f.Service.RecoverAsync();
        Assert.False(result.Ok);
        Assert.Contains("Open League of Legends", result.Message);
        Assert.Equal(0, f.Lcu.HistoryCalls);
        Assert.Equal(0, f.Backups);
        Assert.Null(await f.Database.Games.GetAsync(1001));
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task ConcurrentRequestsSerializeDiscoveryAndSaveEachGameOnlyOnce()
    {
        using var f = await Fixture.CreateAsync();
        f.Lcu.Matches = [Match(1001)];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Lcu.BeforeHistory = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var first = f.Service.RecoverAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = f.Service.RecoverAsync();
        Assert.Equal(1, f.Lcu.HistoryCalls);
        Assert.True(f.Work.HasOutstandingWork);
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.Equal(1, results.Sum(r => r.Recovered));
        Assert.Equal(1, f.Backups);
        Assert.Single(f.Events, e => e.Type == "gamesRecovered");
    }

    [Fact]
    public async Task ShutdownCancelsHistoryReadAndQueuedRecoveryBeforeAnyWrites()
    {
        using var f = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Lcu.BeforeHistory = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        f.Lcu.Matches = [Match(1001)];
        var first = f.Service.RecoverAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = f.Service.RecoverAsync();
        await f.Work.StopAsync(CancellationToken.None);
        Assert.False((await first).Ok);
        Assert.False((await queued).Ok);
        Assert.False((await f.Service.RecoverAsync()).Ok);
        Assert.False(f.Work.HasOutstandingWork);
        Assert.False(f.Work.ShutdownIncomplete);
        Assert.Equal(0, f.Backups);
        Assert.Null(await f.Database.Games.GetAsync(1001));
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task DisconnectDuringLookupDoesNotReportSuccessOrStartBackup()
    {
        using var f = await Fixture.CreateAsync();
        f.Lcu.Matches = [Match(1001)];
        f.Lcu.BeforeHistory = _ => { f.Lcu.Connected = false; return Task.CompletedTask; };
        var result = await f.Service.RecoverAsync();
        Assert.False(result.Ok);
        Assert.Contains("disconnected", result.Message);
        Assert.Equal(0, f.Backups);
        Assert.Null(await f.Database.Games.GetAsync(1001));
    }

    [Fact]
    public async Task FailedHistoryLookupDoesNotClaimThereAreNoMissingGames()
    {
        using var f = await Fixture.CreateAsync();
        f.Lcu.BeforeHistory = _ => throw new HttpRequestException("History is temporarily unavailable");
        var result = await f.Service.RecoverAsync();
        Assert.Equal(0, result.Recovered);
        Assert.Contains("No matches were recovered", result.Message);
        Assert.Contains("wait for League's match history to load", result.Message);
        Assert.Equal(0, f.Backups);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void ResultContractAndIsolatedHostPolicyStayNarrow()
    {
        var value = JsonSerializer.SerializeToElement(new RecentGameRecoveryResult(true, "recovered", 2), SidecarJson.CreateOptions());
        Assert.Equal(new[] { "ok", "message", "recovered" }, value.EnumerateObject().Select(p => p.Name));
        Assert.False(IsolatedHostPolicy.Allows("POST", "/api/settings/recover-games"));
    }

    private static JsonElement Match(long id, int queue = 420, int duration = 1800) => JsonSerializer.SerializeToElement(new
    {
        gameId = id, gameCreation = new DateTimeOffset(Start).ToUnixTimeMilliseconds(), gameDuration = duration,
        gameMode = "CLASSIC", gameType = "MATCHED_GAME", queueId = queue,
        participants = new[] { new { participantId = 1, championId = 103, championName = "Ahri", teamId = 100,
            stats = new { kills = 8, deaths = 3, assists = 6, win = true }, timeline = new { lane = "MIDDLE", role = "SOLO" } } }
    });

    private sealed class Fixture : IDisposable
    {
        public SidecarWriteScope Database { get; } = new();
        public FakeLcu Lcu { get; } = new();
        public SidecarBackgroundWork Work { get; } = new(NullLogger<SidecarBackgroundWork>.Instance);
        public List<(string Type, JsonElement Payload)> Events { get; } = [];
        public int Backups { get; private set; }
        public Func<Task>? BeforeBackup { get; set; }
        public Func<MissedGameCandidate, CancellationToken, Task<RecoveredGameSave>>? SaveGame { get; set; }
        public MissedGameDecisionRepository Decisions { get; }
        public RecentGameRecovery Service { get; }
        private string Root => Path.GetDirectoryName(Database.DatabasePath)!;

        private Fixture()
        {
            var db = Database;
            Decisions = new(db.ConnectionFactory);
            var gameService = new GameService(db.ConnectionFactory, db.Games, db.SessionLog, new RulesRepository(db.ConnectionFactory),
                new GameEventsRepository(db.ConnectionFactory), new DerivedEventsRepository(db.ConnectionFactory),
                db.Config, NullLogger<GameService>.Instance);
            var lifecycle = new GameLifecycleWorkflowService(gameService, Decisions, db.Objectives,
                NullLogger<GameLifecycleWorkflowService>.Instance);
            var history = new MatchHistoryReconciliationService(Lcu, db.Games, Decisions, NullLogger<MatchHistoryReconciliationService>.Instance);
            // The sidecar saves through the game-flow coordinator (MissedGamesFlowTests covers
            // that path); here the lifecycle's own save stands in for it.
            Service = new(Lcu, history, async (candidate, ct) => SaveGame is { } save
                    ? await save(candidate, ct)
                    : (await lifecycle.ReconcileMissedGamesAsync(new([candidate], []), ct)).IngestedCount > 0
                        ? RecoveredGameSave.Saved : RecoveredGameSave.NotSaved,
                db.Games, Work, Backup, (type, payload) => Events.Add((type, JsonSerializer.SerializeToElement(payload))), NullLogger<RecentGameRecovery>.Instance);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Database.InitializeAsync();
            return fixture;
        }

        private async Task Backup() { Backups++; if (BeforeBackup is not null) await BeforeBackup(); }
        public void Dispose() => Database.Dispose();
    }

    private sealed class FakeLcu : ILcuClient
    {
        public bool Connected { get; set; } = true;
        public List<JsonElement> Matches { get; set; } = [];
        public int HistoryCalls { get; private set; }
        public Func<CancellationToken, Task>? BeforeHistory { get; set; }
        public void Configure(LcuCredentials credentials) { }
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(Connected);
        public Task<JsonElement?> GetCurrentSummonerAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<GamePhase> GetGameflowPhaseAsync(CancellationToken ct = default) => Task.FromResult(GamePhase.None);
        public Task<JsonElement?> GetEndOfGameStatsAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<bool> CancelMatchmakingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeclineReadyCheckAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetLobbyQueueIdAsync(CancellationToken ct = default) => Task.FromResult(420);
        public async Task<List<JsonElement>> GetMatchHistoryAsync(int begin = 0, int count = 5, CancellationToken ct = default)
        {
            HistoryCalls++;
            Assert.Equal(10, count);
            if (BeforeHistory is not null) await BeforeHistory(ct);
            return Matches;
        }
        public Task<JsonElement?> GetMatchDetailsAsync(long gameId, CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<string?> GetChampionNameAsync(int championId, CancellationToken ct = default) => Task.FromResult<string?>("Ahri");
        public Task<JsonElement?> GetRankedStatsAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<(string MyChampion, string EnemyLaner, string MyPosition)> GetChampSelectInfoAsync(CancellationToken ct = default) => Task.FromResult(("", "", ""));
        public Task<ChampSelectSnapshot> GetChampSelectSnapshotAsync(CancellationToken ct = default) => Task.FromResult(new ChampSelectSnapshot("", "", "", new Dictionary<string, string>()));
    }
}

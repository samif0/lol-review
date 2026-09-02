using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// v3.7 hard stop — the enforcer over the real write seam (rules, today's games,
/// the hard_stops log) with a fake LCU. Pins the contract the shell relies on:
/// an enforced, tripped rule cancels the queue exactly once per tick window,
/// records the intervention, publishes <c>hardStop</c>, and stays quiet for
/// everything else (not enforced, overridden today, LCU refused, custom).
/// </summary>
public sealed class HardStopEnforcerTests
{
    private static long NowUnix => DateTimeOffset.Now.ToUnixTimeSeconds();

    private sealed class Harness : IDisposable
    {
        public SidecarWriteScope Scope { get; } = new();
        public RulesRepository Rules { get; }
        public HardStopsRepository HardStops { get; }
        public FakeLcu Lcu { get; } = new();
        public SidecarEventHub Hub { get; } = new();
        public LcuLiveState Live { get; } = new();
        public DateTimeOffset Clock { get; set; } = DateTimeOffset.Now;
        public HardStopEnforcer Enforcer { get; }

        public Harness()
        {
            Rules = new RulesRepository(Scope.ConnectionFactory);
            HardStops = new HardStopsRepository(Scope.ConnectionFactory);
            Enforcer = new HardStopEnforcer(
                Rules,
                Scope.Games,
                HardStops,
                Lcu,
                Hub,
                Live,
                NullLogger<HardStopEnforcer>.Instance,
                () => Clock);
        }

        public Task InitializeAsync() => Scope.InitializeAsync();

        public async Task<long> RuleAsync(string type, string condition, bool enforce, string plan = "walk it off")
        {
            var id = await Rules.CreateAsync($"rule {type}", ruleType: type, conditionValue: condition, replacementPlan: plan);
            if (enforce) await Rules.SetEnforceAsync(id, true);
            return id;
        }

        public void Dispose() => Scope.Dispose();
    }

    [Fact]
    public async Task EnforcedMaxGames_Tripped_CancelsQueue_RecordsAndPublishes()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        var ruleId = await h.RuleAsync("max_games", "1", enforce: true);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);
        var (reader, sub) = h.Hub.Subscribe();
        using var _ = sub;

        var snapshot = await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking);

        Assert.NotNull(snapshot);
        Assert.Equal(ruleId, snapshot!.RuleId);
        Assert.Equal(HardStopActions.CancelledQueue, snapshot.Action);
        Assert.Equal("Max 1 games per day", snapshot.ConditionCue);
        Assert.Equal("walk it off", snapshot.ReplacementPlan);
        Assert.True(snapshot.HasPlan);
        Assert.NotNull(snapshot.UnlockAt);
        Assert.True(snapshot.UnlockAt > NowUnix);
        Assert.Equal(1, h.Lcu.CancelCalls);
        Assert.Equal(0, h.Lcu.DeclineCalls);

        // Ledger + replay + SSE.
        var counts = await h.HardStops.GetCountsAsync(0);
        Assert.Equal(new HardStopCounts(Held: 1, Overridden: 0), counts[ruleId]);
        Assert.Equal(snapshot, h.Live.HardStop);
        Assert.True(reader.TryRead(out var evt));
        Assert.Equal("hardStop", evt.Type);
        Assert.Same(snapshot, evt.Payload);
    }

    [Fact]
    public async Task TrippedButNotEnforced_LetsQueueProceed()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        await h.RuleAsync("max_games", "1", enforce: false);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);

        Assert.Null(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        Assert.Equal(0, h.Lcu.CancelCalls);
        Assert.Null(h.Live.HardStop);
        Assert.Empty(await h.HardStops.GetCountsAsync(0));
    }

    [Fact]
    public async Task EnforcedButNotTripped_LetsQueueProceed()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        await h.RuleAsync("max_games", "6", enforce: true);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);

        Assert.Null(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        Assert.Equal(0, h.Lcu.CancelCalls);
    }

    [Fact]
    public async Task ReadyCheck_DeclinesInsteadOfCancelling()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        await h.RuleAsync("max_games", "1", enforce: true);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);

        var snapshot = await h.Enforcer.HandleQueueAsync(GamePhase.ReadyCheck);

        Assert.NotNull(snapshot);
        Assert.Equal(HardStopActions.DeclinedReadyCheck, snapshot!.Action);
        Assert.Equal(1, h.Lcu.DeclineCalls);
        Assert.Equal(0, h.Lcu.CancelCalls);
    }

    [Fact]
    public async Task Override_SilencesTheRuleForTheRestOfTheDay_AndClearsReplay()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        var ruleId = await h.RuleAsync("max_games", "1", enforce: true);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);

        Assert.NotNull(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        await h.Enforcer.OverrideAsync(ruleId);
        Assert.Null(h.Live.HardStop);

        h.Clock = h.Clock.AddSeconds(10); // past the debounce window
        Assert.Null(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        Assert.Equal(1, h.Lcu.CancelCalls);

        var counts = await h.HardStops.GetCountsAsync(0);
        Assert.Equal(new HardStopCounts(Held: 1, Overridden: 1), counts[ruleId]);
    }

    [Fact]
    public async Task LcuRefusal_LeavesNoRecordAndNoLock()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        await h.RuleAsync("max_games", "1", enforce: true);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);
        h.Lcu.Accept = false;

        Assert.Null(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        Assert.Equal(1, h.Lcu.CancelCalls);
        Assert.Null(h.Live.HardStop);
        Assert.Empty(await h.HardStops.GetCountsAsync(0));
    }

    [Fact]
    public async Task SecondTickInsideTheDebounceWindow_IsIgnored()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        await h.RuleAsync("max_games", "1", enforce: true);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);

        Assert.NotNull(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        Assert.Null(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        Assert.Equal(1, h.Lcu.CancelCalls);

        // A re-queue after the window is caught again (and counted again).
        h.Clock = h.Clock.AddSeconds(10);
        Assert.NotNull(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        Assert.Equal(2, h.Lcu.CancelCalls);
        var counts = await h.HardStops.GetCountsAsync(0);
        Assert.Equal(2, counts.Values.Single().Held);
    }

    [Fact]
    public async Task CustomRule_IsNeverEnforced_EvenIfFlagged()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        await h.RuleAsync("custom", "", enforce: true);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);

        Assert.Null(await h.Enforcer.HandleQueueAsync(GamePhase.Matchmaking));
        Assert.Equal(0, h.Lcu.CancelCalls);
    }

    [Fact]
    public async Task NonQueuePhases_AreIgnored()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        await h.RuleAsync("max_games", "1", enforce: true);
        await h.Scope.SeedGameAsync(gameId: 9001, timestamp: NowUnix - 600);

        Assert.Null(await h.Enforcer.HandleQueueAsync(GamePhase.Lobby));
        Assert.Null(await h.Enforcer.HandleQueueAsync(GamePhase.ChampSelect));
        Assert.Equal(0, h.Lcu.CancelCalls);
    }

    [Fact]
    public void HardStopLine_WordsTheRecordNeutrally()
    {
        Assert.Equal("", RulesSnapshotBuilder.BuildHardStopLine(null));
        Assert.Equal("", RulesSnapshotBuilder.BuildHardStopLine(new HardStopCounts(0, 0)));
        Assert.Equal("HELD 3× THIS WEEK", RulesSnapshotBuilder.BuildHardStopLine(new HardStopCounts(3, 0)));
        Assert.Equal("HELD 3× THIS WEEK · OVERRIDDEN 1×", RulesSnapshotBuilder.BuildHardStopLine(new HardStopCounts(3, 1)));
    }

    /// <summary>Minimal ILcuClient: counts the two hard-stop calls, answers
    /// <see cref="Accept"/>, and stubs everything else.</summary>
    private sealed class FakeLcu : ILcuClient
    {
        public bool Accept { get; set; } = true;
        public int CancelCalls { get; private set; }
        public int DeclineCalls { get; private set; }

        public void Configure(LcuCredentials credentials) { }
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<JsonElement?> GetCurrentSummonerAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<GamePhase> GetGameflowPhaseAsync(CancellationToken ct = default) => Task.FromResult(GamePhase.Matchmaking);
        public Task<JsonElement?> GetEndOfGameStatsAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<bool> CancelMatchmakingAsync(CancellationToken ct = default) { CancelCalls++; return Task.FromResult(Accept); }
        public Task<bool> DeclineReadyCheckAsync(CancellationToken ct = default) { DeclineCalls++; return Task.FromResult(Accept); }
        public Task<int> GetLobbyQueueIdAsync(CancellationToken ct = default) => Task.FromResult(420);
        public Task<List<JsonElement>> GetMatchHistoryAsync(int begin = 0, int count = 5, CancellationToken ct = default) => Task.FromResult(new List<JsonElement>());
        public Task<JsonElement?> GetMatchDetailsAsync(long gameId, CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<string?> GetChampionNameAsync(int championId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<JsonElement?> GetRankedStatsAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<(string MyChampion, string EnemyLaner, string MyPosition)> GetChampSelectInfoAsync(CancellationToken ct = default) => Task.FromResult(("", "", ""));
        public Task<ChampSelectSnapshot> GetChampSelectSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new ChampSelectSnapshot("", "", "", new Dictionary<string, string>()));
    }
}

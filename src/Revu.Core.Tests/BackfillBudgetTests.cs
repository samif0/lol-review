using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The Settings backfill runs under a 290 s client deadline. Each leg takes a soft time
/// budget: once it runs out the leg starts no new game, returns its summary (never throws)
/// and reports the games it did not reach, so a deep backlog ends in "run again to
/// continue" instead of an aborted request.
/// </summary>
public sealed class BackfillBudgetTests
{
    private sealed class FailingMatchClient : IRiotMatchClient
    {
        public Action? OnRequest { get; set; }
        public int Requests { get; private set; }
        public List<string> Matches { get; } = new();

        public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
        {
            Requests++;
            Matches.Add(matchId);
            OnRequest?.Invoke();
            return Task.FromResult<JsonElement?>(null);
        }

        public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default)
        {
            Requests++;
            return Task.FromResult<JsonElement?>(null);
        }
    }

    private static TestConfigService Config() => new(new AppConfig { RiotRegion = "na1", RiotPuuid = "self" });

    // Three ranked games with no enemy laner, no laning numbers and no map-state pass yet.
    private static async Task SeedAsync(TestDatabaseScope scope)
    {
        foreach (var id in new long[] { 7_001, 7_002, 7_003 })
        {
            var game = TestGameStatsFactory.Create(id);
            game.EnemyLaner = "";
            game.ParticipantMap = "";
            await scope.Games.SaveAsync(game);
        }
    }

    public static IEnumerable<object[]> Legs() =>
    [
        ["enemy"],
        ["laning"],
        ["mapState"],
    ];

    private static async Task<(int Scanned, int Remaining)> RunAsync(
        string leg, TestDatabaseScope scope, IRiotMatchClient client, CancellationToken budget,
        BackfillAttempts? attempts = null)
    {
        switch (leg)
        {
            case "enemy":
            {
                var r = await new EnemyLanerBackfillService(scope.Games, client, Config(),
                    NullLogger<EnemyLanerBackfillService>.Instance).RunAsync(budget: budget, attempts: attempts);
                return (r.Scanned, r.Remaining);
            }
            case "laning":
            {
                var r = await new LaningBackfillService(scope.Games, client, Config(),
                    NullLogger<LaningBackfillService>.Instance).RunAsync(budget: budget, attempts: attempts);
                return (r.Scanned, r.Remaining);
            }
            default:
            {
                var r = await new MapStateBackfillService(scope.Games, scope.GameEvents, client, Config(),
                    NullLogger<MapStateBackfillService>.Instance).RunAsync(budget: budget, attempts: attempts);
                return (r.Scanned, r.Remaining);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Legs))]
    public async Task SpentBudget_StartsNoGame_AndReportsTheWholeQueueAsRemaining(string leg)
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedAsync(scope);
        var client = new FailingMatchClient();

        var (scanned, remaining) = await RunAsync(leg, scope, client, new CancellationToken(canceled: true));

        Assert.Equal(0, scanned);
        Assert.Equal(3, remaining);
        Assert.Equal(0, client.Requests);
    }

    [Theory]
    [MemberData(nameof(Legs))]
    public async Task BudgetRunningOutMidRun_FinishesTheGameInFlight_ThenStopsWithASummary(string leg)
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedAsync(scope);
        using var budget = new CancellationTokenSource();
        var client = new FailingMatchClient { OnRequest = budget.Cancel };

        var (scanned, remaining) = await RunAsync(leg, scope, client, budget.Token);

        Assert.Equal(1, scanned);
        Assert.Equal(2, remaining);
    }

    // Games that never resolve (manual entries, remakes, matches past retention) used to sit
    // at the head of the newest-first queue: every capped run re-fetched them, never reached
    // the rest, and still said "run it again to continue".
    [Theory]
    [MemberData(nameof(Legs))]
    public async Task GamesTriedEarlierThisSession_GoLast_SoEachCappedRunReachesNewGames(string leg)
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedAsync(scope);
        var attempts = new BackfillAttempts();
        var firstFetched = new List<string>();
        var remainders = new List<int>();

        for (var run = 0; run < 3; run++)
        {
            using var budget = new CancellationTokenSource();
            var client = new FailingMatchClient { OnRequest = budget.Cancel };
            var (scanned, remaining) = await RunAsync(leg, scope, client, budget.Token, attempts);
            Assert.Equal(1, scanned);
            firstFetched.Add(client.Matches[0]);
            remainders.Add(remaining);
        }

        Assert.Equal(3, firstFetched.Distinct().Count());
        Assert.Equal(new[] { 2, 1, 0 }, remainders);

        // Every game tried: a run still retries them (a failure may have been transient) but
        // reports nothing left to reach.
        var again = new FailingMatchClient();
        var (rescanned, left) = await RunAsync(leg, scope, again, CancellationToken.None, attempts);
        Assert.Equal(3, rescanned);
        Assert.Equal(0, left);
    }

    [Theory]
    [MemberData(nameof(Legs))]
    public async Task WithoutAttempts_TheWalkStaysNewestFirst(string leg)
    {
        // The post-game passes rely on the fresh game leading the queue on every retry.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedAsync(scope);
        var first = new List<string>();

        for (var run = 0; run < 2; run++)
        {
            using var budget = new CancellationTokenSource();
            var client = new FailingMatchClient { OnRequest = budget.Cancel };
            await RunAsync(leg, scope, client, budget.Token);
            first.Add(client.Matches[0]);
        }

        Assert.Equal(first[0], first[1]);
    }

    [Fact]
    public async Task NoBudget_DrainsTheQueue_WithNothingRemaining()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedAsync(scope);

        var (scanned, remaining) = await RunAsync("enemy", scope, new FailingMatchClient(), CancellationToken.None);

        Assert.Equal(3, scanned);
        Assert.Equal(0, remaining);
    }
}

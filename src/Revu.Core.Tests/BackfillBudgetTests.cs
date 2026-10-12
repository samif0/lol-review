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

        public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
        {
            Requests++;
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
        string leg, TestDatabaseScope scope, IRiotMatchClient client, CancellationToken budget)
    {
        switch (leg)
        {
            case "enemy":
            {
                var r = await new EnemyLanerBackfillService(scope.Games, client, Config(),
                    NullLogger<EnemyLanerBackfillService>.Instance).RunAsync(budget: budget);
                return (r.Scanned, r.Remaining);
            }
            case "laning":
            {
                var r = await new LaningBackfillService(scope.Games, client, Config(),
                    NullLogger<LaningBackfillService>.Instance).RunAsync(budget: budget);
                return (r.Scanned, r.Remaining);
            }
            default:
            {
                var r = await new MapStateBackfillService(scope.Games, scope.GameEvents, client, Config(),
                    NullLogger<MapStateBackfillService>.Instance).RunAsync(budget: budget);
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

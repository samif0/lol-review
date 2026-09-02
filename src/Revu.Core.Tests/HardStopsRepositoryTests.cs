using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>v3.7: the hard_stops intervention log — append, "overridden since",
/// and the per-rule held/overridden counts the Rules page shows.</summary>
public sealed class HardStopsRepositoryTests
{
    [Fact]
    public async Task Record_ThenCounts_SplitHeldFromOverridden()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var repo = new HardStopsRepository(scope.ConnectionFactory);

        const long t0 = 1_800_000_000;
        await repo.RecordAsync(1, HardStopActions.CancelledQueue, "Already played 6/6 games today", t0);
        await repo.RecordAsync(1, HardStopActions.DeclinedReadyCheck, "Already played 6/6 games today", t0 + 10);
        await repo.RecordAsync(1, HardStopActions.Override, "", t0 + 20);
        await repo.RecordAsync(2, HardStopActions.CancelledQueue, "2 consecutive losses", t0 + 30);
        // Older than the window — must not count.
        await repo.RecordAsync(2, HardStopActions.CancelledQueue, "stale", t0 - 100);

        var counts = await repo.GetCountsAsync(t0);

        Assert.Equal(new HardStopCounts(Held: 2, Overridden: 1), counts[1]);
        Assert.Equal(new HardStopCounts(Held: 1, Overridden: 0), counts[2]);
        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public async Task OverriddenRuleIds_HonorsTheSinceBound()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var repo = new HardStopsRepository(scope.ConnectionFactory);

        const long today = 1_800_000_000;
        await repo.RecordAsync(1, HardStopActions.Override, "", today - 3600); // yesterday
        await repo.RecordAsync(2, HardStopActions.Override, "", today + 60);
        await repo.RecordAsync(3, HardStopActions.CancelledQueue, "held", today + 60); // not an override

        var ids = await repo.GetOverriddenRuleIdsAsync(today);

        Assert.Equal(new HashSet<long> { 2 }, ids);
    }

    [Fact]
    public async Task Empty_ReturnsEmptyCollections()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var repo = new HardStopsRepository(scope.ConnectionFactory);

        Assert.Empty(await repo.GetOverriddenRuleIdsAsync(0));
        Assert.Empty(await repo.GetCountsAsync(0));
    }
}

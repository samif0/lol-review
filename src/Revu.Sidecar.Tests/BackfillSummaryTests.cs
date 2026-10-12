using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>The Settings backfill result line reports what is left instead of failing.</summary>
public sealed class BackfillSummaryTests
{
    [Fact]
    public void ACappedRun_SaysHowManyGamesAreStillWaiting()
    {
        var (text, remaining) = SidecarEndpoints.BackfillSummary(
            new EnemyLanerBackfillResult(40, 30, 5, 5, Remaining: 120),
            new LaningBackfillResult(20, 18, 1, 1, Remaining: 300),
            new MapStateBackfillResult(4, 4, 0, 0, Remaining: 0));

        Assert.Equal(300, remaining);
        Assert.StartsWith("Backfilled 52 game(s).", text);
        Assert.EndsWith("At least 300 more games are waiting. Run it again to continue.", text);
    }

    [Fact]
    public void AFinishedRun_SaysSoWithoutARemainder()
    {
        var (text, remaining) = SidecarEndpoints.BackfillSummary(
            new EnemyLanerBackfillResult(0, 0, 0, 0), new LaningBackfillResult(0, 0, 0, 0), new MapStateBackfillResult(0, 0, 0, 0));

        Assert.Equal(0, remaining);
        Assert.Equal("Nothing to backfill. Every game already has its matchup data.", text);
        Assert.DoesNotContain("—", text);
    }

    [Fact]
    public void ARunThatResolvedNothingButStoppedEarly_StillOffersToContinue()
    {
        var (text, _) = SidecarEndpoints.BackfillSummary(
            new EnemyLanerBackfillResult(3, 0, 0, 3, Remaining: 1), new LaningBackfillResult(0, 0, 0, 0), new MapStateBackfillResult(0, 0, 0, 0));

        Assert.Equal("No new match data found in this pass. At least 1 more game is waiting. Run it again to continue.", text);
    }

    [Fact]
    public void EveryLegFitsTheClientDeadline()
    {
        // run_backfill waits 290 s; three legs plus each one's game in flight must fit.
        Assert.True(SidecarEndpoints.BackfillLegBudget * 3 <= TimeSpan.FromSeconds(200));
    }
}

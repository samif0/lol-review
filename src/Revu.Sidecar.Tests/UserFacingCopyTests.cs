using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Copy the sidecar sends for display follows the house style: sentence case and no
/// em dashes. The page renders these strings as they arrive.
/// </summary>
public sealed class UserFacingCopyTests
{
    [Fact]
    public void MatchupAndDeathCauseHints_HaveNoEmDash()
    {
        string[] copy =
        [
            MatchupsSnapshotBuilder.EnemyLookupHint,
            MatchupsSnapshotBuilder.EnemyManualHint,
            MatchupsSnapshotBuilder.LaneGuessHint,
            .. DeathClasses.All.Select(entry => entry.Hint),
        ];
        Assert.All(copy, text => Assert.DoesNotContain('—', text));
    }

    [Fact]
    public void FullGameShareFallbackCopy_HasNoDashes()
    {
        string[] copy = [.. VodShareWorker.ErrorCodes.Select(VodShareWorker.Message), VodShareWorker.Message("unknown_code")];
        Assert.All(copy, text =>
        {
            Assert.DoesNotContain('—', text);
            Assert.DoesNotContain('–', text);
        });
    }

    [Fact]
    public async Task DashboardStatSubs_AreSentenceCase()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var dto = await new DashboardSnapshotBuilder(
            scope.SessionLog, scope.Objectives, scope.Vod, scope.Evidence, scope.DeathClassifications,
            new RulesRepository(scope.ConnectionFactory), scope.Games, scope.CoachingStints, scope.Config,
            NullLogger<DashboardSnapshotBuilder>.Instance).BuildAsync();

        Assert.Equal("No active streak", dto.Stats.AdherenceSub);
        Assert.Equal("None yet", dto.Stats.PatternsReviewedSub);
        Assert.Equal("Start a session", dto.NextStep.CtaLabel);
        Assert.DoesNotContain('—', dto.NextStep.Kicker + dto.NextStep.Title + dto.NextStep.Detail);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Sidecar;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// v3.3 coaching stints on the dashboard snapshot: GET /api/dashboard must
/// carry the active stint + its block counts, stamp the open block's coach tag
/// and within-stint number on the intent, and drop with-coach block games from
/// the unreviewed nag — all through the same DashboardSnapshotBuilder the
/// endpoint delegates to.
/// </summary>
public sealed class DashboardStintSnapshotTests
{
    [Fact]
    public async Task NoStint_SnapshotHasNullStint_AndPlainIntent()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var dto = await BuildDashboardAsync(scope);

        Assert.Null(dto.Stint);
        Assert.False(dto.Intent.WithCoach);
        Assert.Null(dto.Intent.StintBlockNumber);
    }

    [Fact]
    public async Task ActiveStint_WithCoachBlock_FlowsThroughSnapshot()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var stintId = await scope.CoachingStints.StartStintAsync("Violet", today, "2026-12-30");

        // Mirror POST /api/block/start with { withCoach: true } while the
        // stint is active: stamp id + next block number on today's block.
        var blockNumber = await scope.CoachingStints.GetNextBlockNumberAsync(stintId);
        await scope.SessionLog.SetSessionIntentionAsync(
            today, "coach vod review", withCoach: true, stintId: stintId, stintBlockNumber: blockNumber);

        // A game played today would normally sit in the unreviewed nag; the
        // with-coach block exempts it (session_log untouched).
        await scope.SeedGameAsync(gameId: 9001, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var dto = await BuildDashboardAsync(scope);

        Assert.NotNull(dto.Stint);
        Assert.Equal("Violet", dto.Stint!.Name);
        Assert.Equal(1, dto.Stint.BlocksTotal);
        Assert.Equal(1, dto.Stint.BlocksWithCoach);
        Assert.Equal(0, dto.Stint.BlocksSolo);

        Assert.Equal("coach vod review", dto.Intent.SessionIntention);
        Assert.True(dto.Intent.WithCoach);
        Assert.Equal(1, dto.Intent.StintBlockNumber);

        Assert.Equal(0, dto.Unreviewed.Count);
        Assert.DoesNotContain(dto.Unreviewed.Items, i => i.GameId == 9001);
    }

    [Fact]
    public async Task ActiveStint_SoloBlock_KeepsUnreviewedNag()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var stintId = await scope.CoachingStints.StartStintAsync("Violet", today);
        await scope.SessionLog.SetSessionIntentionAsync(
            today, "solo grind", withCoach: false, stintId: stintId, stintBlockNumber: 1);

        await scope.SeedGameAsync(gameId: 9101, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var dto = await BuildDashboardAsync(scope);

        Assert.NotNull(dto.Stint);
        Assert.Equal(0, dto.Stint!.BlocksWithCoach);
        Assert.Equal(1, dto.Stint.BlocksSolo);
        Assert.False(dto.Intent.WithCoach);

        // Solo block: the game still nags for an in-app review.
        Assert.Contains(dto.Unreviewed.Items, i => i.GameId == 9101);
    }

    // The builder is the seam behind GET /api/dashboard (no HTTP host in tests
    // — house style). RulesRepository isn't on the scope; construct it here.
    private static Task<DashboardDto> BuildDashboardAsync(SidecarWriteScope scope)
    {
        var builder = new DashboardSnapshotBuilder(
            scope.SessionLog,
            scope.Objectives,
            scope.Vod,
            scope.Evidence,
            scope.DeathClassifications,
            new RulesRepository(scope.ConnectionFactory),
            scope.Games,
            scope.CoachingStints,
            scope.Config,
            NullLogger<DashboardSnapshotBuilder>.Instance);
        return builder.BuildAsync();
    }
}

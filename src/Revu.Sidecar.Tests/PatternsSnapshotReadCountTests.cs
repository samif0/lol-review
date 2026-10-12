using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>The Patterns page reads every card's playlist in one query, not one per card.</summary>
public sealed class PatternsSnapshotReadCountTests
{
    private sealed class CountingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public int Opened { get; private set; }
        public string DatabasePath => inner.DatabasePath;
        public SqliteConnection CreateConnection() { Opened++; return inner.CreateConnection(); }
    }

    private static async Task<(int Opened, PatternsSnapshotDto Snapshot)> BuildAsync(int objectives)
    {
        using var scope = new SidecarWriteScope();
        await SeedAsync(scope, objectives);

        var counting = new CountingFactory(scope.ConnectionFactory);
        var snapshot = await new PatternsSnapshotBuilder(new EvidenceRepository(counting),
            NullLogger<PatternsSnapshotBuilder>.Instance).BuildAsync();
        return (counting.Opened, snapshot);
    }

    private static async Task<(int Opened, DashboardDto Dashboard)> BuildDashboardAsync(int objectives)
    {
        using var scope = new SidecarWriteScope();
        await SeedAsync(scope, objectives);

        // Only the evidence reads are counted: that is where the per-card queries were.
        var counting = new CountingFactory(scope.ConnectionFactory);
        var dashboard = await new DashboardSnapshotBuilder(
            scope.SessionLog, scope.Objectives, scope.Vod, new EvidenceRepository(counting), scope.DeathClassifications,
            new RulesRepository(scope.ConnectionFactory), scope.Games, scope.CoachingStints, scope.Config,
            NullLogger<DashboardSnapshotBuilder>.Instance).BuildAsync();
        return (counting.Opened, dashboard);
    }

    private static async Task SeedAsync(SidecarWriteScope scope, int objectives)
    {
        await scope.InitializeAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8_801, timestamp: now - 3_600));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8_802, timestamp: now - 60));
        for (var i = 0; i < objectives; i++)
        {
            var objectiveId = await scope.Objectives.CreateAsync($"Objective {i}", "macro");
            foreach (var gameId in new long[] { 8_801, 8_802 })
            {
                await scope.Evidence.UpsertAsync(new EvidenceUpsert(gameId, EvidenceKinds.Clip, null, $"clip:test:{gameId}:{i}",
                    100 + i, 120 + i, "Saved clip", ObjectiveId: objectiveId, Polarity: EvidencePolarities.Bad,
                    Status: EvidenceStatuses.Evidence));
            }
        }
    }

    [Fact]
    public async Task ThePageCostsTheSameReadsForOneObjectiveOrSix()
    {
        var (oneOpened, one) = await BuildAsync(1);
        var (sixOpened, six) = await BuildAsync(6);

        Assert.Equal(2, one.Patterns.Count);   // saved collection + recurring-mistakes trend
        Assert.Equal(12, six.Patterns.Count);
        Assert.All(six.Patterns, card => Assert.Equal(2, card.TotalMomentCount));
        Assert.Equal(oneOpened, sixOpened);
        Assert.Equal("", six.ErrorText);
        Assert.Contains(six.Patterns, card => card.Kind == PatternConstants.KindBadObjectiveEvidence);
    }

    [Fact]
    public async Task TheDashboardNagCostsTheSameReadsForOneObjectiveOrSix()
    {
        var (oneOpened, one) = await BuildDashboardAsync(1);
        var (sixOpened, six) = await BuildDashboardAsync(6);

        Assert.Single(one.Patterns.Items);
        Assert.Equal(4, six.Patterns.Items.Count); // the nag still shows at most four pending trends
        Assert.All(six.Patterns.Items, item => Assert.Equal(PatternConstants.KindBadObjectiveEvidence, item.Kind));
        Assert.Equal(oneOpened, sixOpened);
    }
}

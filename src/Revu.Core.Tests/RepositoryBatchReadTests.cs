using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

public sealed class RepositoryBatchReadTests
{
    [Fact]
    public async Task BatchObjectiveAndBookmarkReads_ReturnOnlyMatchingGameIds()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Track objective setup", "macro");
        const long practicedGameId = 900001;
        const long unpracticedGameId = 900002;
        const long untaggedGameId = 900003;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(practicedGameId, champion: "Ahri", win: true));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(unpracticedGameId, champion: "Kai'Sa", win: false));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(untaggedGameId, champion: "Lux", win: true));

        await scope.Objectives.RecordGameAsync(practicedGameId, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(unpracticedGameId, objectiveId, practiced: false);

        await scope.Vod.AddBookmarkAsync(practicedGameId, 900, "Dragon setup", objectiveId: objectiveId);
        await scope.Vod.AddBookmarkAsync(untaggedGameId, 600, "No tag");

        var ids = new[] { practicedGameId, unpracticedGameId, untaggedGameId };
        var practicedIds = await scope.Objectives.GetGamesWithPracticedObjectivesAsync(ids);
        var taggedBookmarkIds = await scope.Vod.GetGamesWithObjectiveTaggedBookmarksAsync(ids);

        Assert.Contains(practicedGameId, practicedIds);
        Assert.DoesNotContain(unpracticedGameId, practicedIds);
        Assert.DoesNotContain(untaggedGameId, practicedIds);

        Assert.Contains(practicedGameId, taggedBookmarkIds);
        Assert.DoesNotContain(unpracticedGameId, taggedBookmarkIds);
        Assert.DoesNotContain(untaggedGameId, taggedBookmarkIds);
    }

    /// <summary>
    /// v2.17.5 regression: timeline-region evidence (attached on ReviewPage)
    /// writes to evidence_items, not vod_bookmarks. The dashboard's
    /// "VOD evidence pending" warning used to ignore that table and false-fire
    /// on games that already had timeline-region evidence with an objective.
    /// </summary>
    [Fact]
    public async Task GetGamesWithObjectiveTaggedBookmarksAsync_CountsTimelineRegionEvidence()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Track objective setup", "macro");
        const long timelineEvidenceGameId = 901001; // has only timeline_region evidence
        const long dismissedEvidenceGameId = 901002; // evidence_items row but dismissed → should NOT count
        const long noEvidenceGameId = 901003;        // nothing at all
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(timelineEvidenceGameId, champion: "Sivir", win: true));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(dismissedEvidenceGameId, champion: "Caitlyn", win: false));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(noEvidenceGameId, champion: "Ezreal", win: true));

        // Timeline-region evidence on ReviewPage: writes evidence_items only.
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: timelineEvidenceGameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "tl:1",
            StartTimeSeconds: 600,
            EndTimeSeconds: 660,
            Title: "Botlane skirmish",
            ObjectiveId: objectiveId,
            Status: EvidenceStatuses.Evidence));

        // Dismissed row should not satisfy the check.
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: dismissedEvidenceGameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "tl:2",
            StartTimeSeconds: 300,
            EndTimeSeconds: 360,
            Title: "Discarded",
            ObjectiveId: objectiveId,
            Status: EvidenceStatuses.Dismissed));

        var ids = new[] { timelineEvidenceGameId, dismissedEvidenceGameId, noEvidenceGameId };
        var taggedIds = await scope.Vod.GetGamesWithObjectiveTaggedBookmarksAsync(ids);

        Assert.Contains(timelineEvidenceGameId, taggedIds);
        Assert.DoesNotContain(dismissedEvidenceGameId, taggedIds);
        Assert.DoesNotContain(noEvidenceGameId, taggedIds);
    }
}

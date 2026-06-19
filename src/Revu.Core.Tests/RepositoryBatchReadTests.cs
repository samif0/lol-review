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

    /// <summary>
    /// 2026-06-19 perf (brief 19-05): ReviewSnapshotBuilder used to re-query
    /// prompts once per active objective (an N+1). GetPromptsForObjectivesAsync
    /// is the batched replacement — assert it groups prompts by objective id,
    /// keeps each group's sort_order-then-id ordering (identical to the
    /// single-objective method), and only returns requested objectives.
    /// </summary>
    [Fact]
    public async Task GetPromptsForObjectivesAsync_GroupsPromptsPerObjectiveIdInSortOrder()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objWithPrompts = await scope.Objectives.CreateWithPhasesAsync(
            "Wave management", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);
        var objSinglePrompt = await scope.Objectives.CreateWithPhasesAsync(
            "Vision control", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);
        var objNoPrompts = await scope.Objectives.CreateWithPhasesAsync(
            "Trading stance", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);

        // First-created has the highest sort_order so the result ordering can't
        // accidentally match creation order.
        var firstCreatedLastSort = await scope.Prompts.CreatePromptAsync(
            objWithPrompts, ObjectivePhases.InGame, "last", 10);
        var secondCreatedFirstSort = await scope.Prompts.CreatePromptAsync(
            objWithPrompts, ObjectivePhases.InGame, "first", 0);
        var thirdCreatedMidSort = await scope.Prompts.CreatePromptAsync(
            objWithPrompts, ObjectivePhases.InGame, "mid", 5);
        var singlePrompt = await scope.Prompts.CreatePromptAsync(
            objSinglePrompt, ObjectivePhases.InGame, "only", 0);

        // Includes an objective with no prompts; the batch must simply omit it.
        var grouped = await scope.Prompts.GetPromptsForObjectivesAsync(
            new[] { objWithPrompts, objSinglePrompt, objNoPrompts });

        // Objectives with no prompts are absent from the dictionary.
        Assert.Equal(2, grouped.Count);
        Assert.DoesNotContain(objNoPrompts, grouped.Keys);

        // Multi-prompt objective: grouped under its id, ordered by sort_order then id.
        var promptsForObj = grouped[objWithPrompts];
        Assert.Equal(3, promptsForObj.Count);
        Assert.Equal(secondCreatedFirstSort, promptsForObj[0].Id); // sort_order 0
        Assert.Equal(thirdCreatedMidSort, promptsForObj[1].Id);    // sort_order 5
        Assert.Equal(firstCreatedLastSort, promptsForObj[2].Id);   // sort_order 10
        Assert.All(promptsForObj, p => Assert.Equal(objWithPrompts, p.ObjectiveId));

        // Single-prompt objective keeps its own bucket — no cross-contamination.
        var singleBucket = Assert.Single(grouped[objSinglePrompt]);
        Assert.Equal(singlePrompt, singleBucket.Id);

        // Parity with the per-objective method the batch replaces.
        var perObjective = await scope.Prompts.GetPromptsForObjectiveAsync(objWithPrompts);
        Assert.Equal(
            perObjective.Select(p => p.Id),
            grouped[objWithPrompts].Select(p => p.Id));
    }

    [Fact]
    public async Task GetPromptsForObjectivesAsync_EmptyInput_ReturnsEmpty()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var grouped = await scope.Prompts.GetPromptsForObjectivesAsync(Array.Empty<long>());

        Assert.Empty(grouped);
    }
}

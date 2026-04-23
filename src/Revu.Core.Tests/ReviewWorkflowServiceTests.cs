using System.Text.Json;
using Revu.Core.Models;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

public sealed class ReviewWorkflowServiceTests
{
    [Fact]
    public async Task LoadAsync_RestoresDraftAndLoadsMatchupHistory()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var previousGameId = 2001L;
        var currentGameId = 2002L;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(previousGameId, champion: "Ahri", timestamp: 1_710_000_000));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(currentGameId, champion: "Ahri", timestamp: 1_710_000_600));

        var objectiveId = await scope.Objectives.CreateAsync("Trade around cooldowns", "lane", completionCriteria: "Punish after spell use");
        var tagId = (await scope.ConceptTags.GetAllAsync()).First().Id;

        await scope.MatchupNotes.UpsertForGameAsync(previousGameId, "Ahri", "Syndra", "Respect level 1 spacing");
        await scope.ReviewDrafts.UpsertAsync(new ReviewDraft
        {
            GameId = currentGameId,
            ReviewNotes = "Draft note",
            EnemyLaner = "Syndra",
            MatchupNote = "Save wave for level 6",
            SelectedTagIdsJson = JsonSerializer.Serialize(new[] { tagId }),
            ObjectiveAssessmentsJson = JsonSerializer.Serialize(new[]
            {
                new SaveObjectivePracticeRequest(objectiveId, true, "Tracked her E")
            }),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var workflow = CreateWorkflow(scope, requireReviewNotes: false);
        var screenData = await workflow.LoadAsync(currentGameId);

        Assert.NotNull(screenData);
        Assert.Equal("Draft note", screenData!.Snapshot.ReviewNotes);
        Assert.Equal("Syndra", screenData.Snapshot.EnemyLaner);
        Assert.Equal("Save wave for level 6", screenData.Snapshot.MatchupNote);
        Assert.Contains(tagId, screenData.Snapshot.SelectedTagIds);

        var objective = Assert.Single(screenData.ObjectiveAssessments);
        Assert.True(objective.Practiced);
        Assert.Equal("Tracked her E", objective.ExecutionNote);

        var history = Assert.Single(screenData.MatchupHistory);
        Assert.Equal("Respect level 1 spacing", history.Note);
        Assert.Equal(previousGameId, history.GameId);
    }

    [Fact]
    public async Task SaveAsync_RejectsMatchupNoteWithoutEnemyChampion()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 3001;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));

        var workflow = CreateWorkflow(scope, requireReviewNotes: false);
        var result = await workflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Ahri",
            Win: true,
            RequireReviewNotes: false,
            Snapshot: EmptySnapshot with { MatchupNote = "Need a note first" }));

        Assert.False(result.Success);
        Assert.Equal("Add the enemy champion before saving a matchup note.", result.ErrorMessage);
    }

    [Fact]
    public async Task SaveAsync_RejectsEmptyReviewWhenReviewNotesAreRequired()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 3002;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));

        var workflow = CreateWorkflow(scope, requireReviewNotes: true);
        var result = await workflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Ahri",
            Win: true,
            RequireReviewNotes: true,
            Snapshot: EmptySnapshot));

        Assert.False(result.Success);
        Assert.Equal("Review notes are required in Settings. Add review content before saving.", result.ErrorMessage);
    }

    [Fact]
    public async Task SaveAsync_PersistsReviewSessionTagsObjectivesAndMatchupNote()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 4001;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Orianna", win: false));

        var objectiveId = await scope.Objectives.CreateAsync("Hold TP window", "macro", completionCriteria: "Crash before roam");
        var tagId = (await scope.ConceptTags.GetAllAsync()).First().Id;
        await scope.ReviewDrafts.UpsertAsync(new ReviewDraft { GameId = gameId, ReviewNotes = "stale draft" });

        var workflow = CreateWorkflow(scope, requireReviewNotes: true);
        var snapshot = EmptySnapshot with
        {
            MentalRating = 7,
            WentWell = "Played waves well",
            Mistakes = "Missed one flash punish",
            FocusNext = "Track jungle timers",
            ReviewNotes = "Strong reset discipline overall",
            ImprovementNote = "Keep lane warded",
            Attribution = "Own mistakes",
            MentalHandled = "Reset after death",
            SpottedProblems = "Late swap to side lane",
            OutsideControl = "Enemy jungle pathing",
            WithinControl = "Wave state",
            PersonalContribution = "Did not cover flank",
            EnemyLaner = "Syndra",
            MatchupNote = "Respect level 3 burst",
            SelectedTagIds = [tagId],
            ObjectivePractices =
            [
                new SaveObjectivePracticeRequest(objectiveId, true, "Held wave before base")
            ],
        };

        var result = await workflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Orianna",
            Win: false,
            RequireReviewNotes: true,
            Snapshot: snapshot));

        Assert.True(result.Success);
        Assert.Equal("Syndra", result.SavedEnemyLaner);

        var savedGame = await scope.Games.GetAsync(gameId);
        var sessionEntry = await scope.SessionLog.GetEntryAsync(gameId);
        var tagIds = await scope.ConceptTags.GetIdsForGameAsync(gameId);
        var matchupNote = await scope.MatchupNotes.GetForGameAsync(gameId);
        var objectiveRecords = await scope.Objectives.GetGameObjectivesAsync(gameId);
        var draft = await scope.ReviewDrafts.GetAsync(gameId);

        Assert.NotNull(savedGame);
        Assert.Equal("Strong reset discipline overall", savedGame!.ReviewNotes);
        Assert.Equal("Played waves well", savedGame.WentWell);
        Assert.Equal("Track jungle timers", savedGame.FocusNext);
        Assert.Equal("Syndra", savedGame.EnemyLaner);

        Assert.NotNull(sessionEntry);
        Assert.Equal(7, sessionEntry!.MentalRating);
        Assert.Equal("Keep lane warded", sessionEntry.ImprovementNote);
        Assert.Equal("Reset after death", sessionEntry.MentalHandled);

        Assert.Equal([tagId], tagIds);

        Assert.NotNull(matchupNote);
        Assert.Equal("Respect level 3 burst", matchupNote!.Note);

        var objective = Assert.Single(objectiveRecords);
        Assert.Equal(objectiveId, objective.ObjectiveId);
        Assert.True(objective.Practiced);
        Assert.Equal("Held wave before base", objective.ExecutionNote);

        Assert.Null(draft);
    }

    [Fact]
    public async Task LoadAsync_IncludesAllActiveObjectivesInPostGameAssessmentList()
    {
        // All active objectives — regardless of phase — are shown in the
        // post-game review so the user can mark progress on anything in flight.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 4101;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Orianna", win: true));

        var preGameObjectiveId = await scope.Objectives.CreateAsync(
            "Set loading-screen plan",
            "prep",
            completionCriteria: "Name first three waves",
            phase: ObjectivePhases.PreGame);
        var inGameObjectiveId = await scope.Objectives.CreateAsync(
            "Punish cooldowns",
            "lane",
            completionCriteria: "Trade after key spell use",
            phase: ObjectivePhases.InGame);
        var postGameObjectiveId = await scope.Objectives.CreateAsync(
            "Write one review takeaway",
            "review",
            completionCriteria: "Capture one fix",
            phase: ObjectivePhases.PostGame);

        var workflow = CreateWorkflow(scope, requireReviewNotes: false);
        var screenData = await workflow.LoadAsync(gameId);

        Assert.NotNull(screenData);
        Assert.Contains(screenData!.ObjectiveAssessments, item => item.ObjectiveId == preGameObjectiveId);
        Assert.Contains(screenData.ObjectiveAssessments, item => item.ObjectiveId == inGameObjectiveId);
        Assert.Contains(screenData.ObjectiveAssessments, item => item.ObjectiveId == postGameObjectiveId);
    }

    [Fact]
    public async Task SaveAsync_PersistsMentalRatingToDb_ReviewIsReadableAfterSave()
    {
        // Critical: verifies the mental_rating column is written on save — the field
        // that marks a game as reviewed and gates the post-game page transition.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 5001;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Ahri", win: true));

        var workflow = CreateWorkflow(scope, requireReviewNotes: false);
        var result = await workflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Ahri",
            Win: true,
            RequireReviewNotes: false,
            Snapshot: EmptySnapshot with { MentalRating = 8, WentWell = "Good wave reads" }));

        Assert.True(result.Success);

        // Read back from DB — went_well must be persisted on the game row
        var savedGame = await scope.Games.GetAsync(gameId);
        Assert.NotNull(savedGame);
        Assert.Equal("Good wave reads", savedGame!.WentWell);

        // Session log entry must exist with mental_rating (drives session summary + "has review" flag)
        var sessionEntry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(sessionEntry);
        Assert.Equal(8, sessionEntry!.MentalRating);
    }

    [Fact]
    public async Task SaveAsync_ObjectivePracticeUpdatesScoreInDb()
    {
        // Critical: verifies that marking an objective as practiced during review
        // actually increments the score in the objectives table.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 5002;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Jinx", win: true));
        var objectiveId = await scope.Objectives.CreateAsync("Improve wave management", "laning");

        var initialObjective = await scope.Objectives.GetAsync(objectiveId);
        Assert.Equal(0, initialObjective!.Score);
        Assert.Equal(0, initialObjective.GameCount);

        var workflow = CreateWorkflow(scope, requireReviewNotes: false);
        var result = await workflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Jinx",
            Win: true,
            RequireReviewNotes: false,
            Snapshot: EmptySnapshot with
            {
                MentalRating = 7,
                ObjectivePractices = [new SaveObjectivePracticeRequest(objectiveId, true, "Crashed before roam")],
            }));

        Assert.True(result.Success);

        var updatedObjective = await scope.Objectives.GetAsync(objectiveId);
        Assert.Equal(1, updatedObjective!.GameCount);
        Assert.True(updatedObjective.Score > 0); // practiced win = +2 pts

        var gameObjectives = await scope.Objectives.GetGameObjectivesAsync(gameId);
        var record = Assert.Single(gameObjectives);
        Assert.Equal(objectiveId, record.ObjectiveId);
        Assert.True(record.Practiced);
        Assert.Equal("Crashed before roam", record.ExecutionNote);
    }

    private static ReviewWorkflowService CreateWorkflow(TestDatabaseScope scope, bool requireReviewNotes)
    {
        return new ReviewWorkflowService(
            scope.Games,
            scope.ConceptTags,
            scope.Vod,
            new StubVodService(),
            scope.SessionLog,
            scope.Objectives,
            scope.ReviewDrafts,
            scope.MatchupNotes,
            new TestConfigService(new AppConfig { RequireReviewNotes = requireReviewNotes }),
            new NullCoachSidecarNotifier(),
            NullLogger<ReviewWorkflowService>.Instance);
    }

    private static readonly ReviewSnapshot EmptySnapshot = new(
        MentalRating: 5,
        WentWell: "",
        Mistakes: "",
        FocusNext: "",
        ReviewNotes: "",
        ImprovementNote: "",
        Attribution: "",
        MentalHandled: "",
        SpottedProblems: "",
        OutsideControl: "",
        WithinControl: "",
        PersonalContribution: "",
        EnemyLaner: "",
        MatchupNote: "",
        SelectedTagIds: [],
        ObjectivePractices: []);
}

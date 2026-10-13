using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

/// <summary>
/// The review save and the review delete are one transaction each: a write that fails
/// partway leaves the game exactly as it was, never "reviewed" with half its rows.
/// Failures are injected with SQLite triggers so the real repositories run unchanged.
/// </summary>
public sealed class ReviewSaveTransactionTests
{
    [Fact]
    public async Task SaveAsync_LateWriteFails_RollsBackEveryEarlierWrite()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 9101;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Orianna", win: true));
        var objectiveId = await scope.Objectives.CreateAsync("Hold TP window", "macro");
        var tagId = (await scope.ConceptTags.GetAllAsync()).First().Id;
        // Concept tags are written near the end of the save; make that write fail.
        await ExecAsync(scope, """
            CREATE TRIGGER fail_tags BEFORE INSERT ON game_concept_tags
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        var result = await CreateWorkflow(scope).SaveAsync(new SaveReviewRequest(gameId, "Orianna", true, false,
            Snapshot with
            {
                ReviewNotes = "Strong reset discipline",
                ImprovementNote = "Keep lane warded",
                EnemyLaner = "Syndra",
                MatchupNote = "Respect level 3 burst",
                SelectedTagIds = [tagId],
                ObjectivePractices = [new SaveObjectivePracticeRequest(objectiveId, true, "Held wave")],
            }));

        Assert.False(result.Success);
        var game = await scope.Games.GetAsync(gameId);
        Assert.Equal("", game!.ReviewNotes);
        Assert.Equal(0, game.Rating);
        Assert.Equal("", game.EnemyLaner);
        Assert.Null(await scope.SessionLog.GetEntryAsync(gameId));
        Assert.Null(await scope.MatchupNotes.GetForGameAsync(gameId));
        Assert.Empty(await scope.Objectives.GetGameObjectivesAsync(gameId));
        var objective = await scope.Objectives.GetAsync(objectiveId);
        Assert.Equal(0, objective!.Score);
        Assert.Equal(0, objective.GameCount);
    }

    [Fact]
    public async Task SaveAsync_WritesChampionAndResultFromTheStoredGame_NotTheRequest()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 9102;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Ahri", win: true));

        // A stale page echoes the wrong champion and result back.
        var result = await CreateWorkflow(scope).SaveAsync(new SaveReviewRequest(gameId, "Zed", false, false,
            Snapshot with { ReviewNotes = "fine", EnemyLaner = "Syndra", MatchupNote = "Short trades" }));

        Assert.True(result.Success);
        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.Equal("Ahri", entry!.ChampionName);
        Assert.True(entry.Win);
        Assert.Equal("Ahri", (await scope.MatchupNotes.GetForGameAsync(gameId))!.Champion);
    }

    [Fact]
    public async Task SaveAsync_UnknownGame_FailsWithoutWritingAnything()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var result = await CreateWorkflow(scope).SaveAsync(new SaveReviewRequest(9103, "Ahri", true, false,
            Snapshot with { ReviewNotes = "orphan" }));

        Assert.False(result.Success);
        Assert.Null(await scope.SessionLog.GetEntryAsync(9103));
    }

    [Fact]
    public async Task SaveAsync_CriteriaStampFails_StillSavesTheReview()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 9104;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));
        var objectiveId = await scope.Objectives.CreateAsync("Farm", "lane");
        await scope.Objectives.UpdateCriteriaAsync(objectiveId, "cs_per_min", ">=", 1);
        await ExecAsync(scope, """
            CREATE TRIGGER fail_criteria BEFORE UPDATE OF criteria_met ON game_objectives
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        var result = await CreateWorkflow(scope).SaveAsync(new SaveReviewRequest(gameId, "Ahri", true, false,
            Snapshot with
            {
                ReviewNotes = "kept",
                ObjectivePractices = [new SaveObjectivePracticeRequest(objectiveId, true, "")],
            }));

        // The criteria verdict is a best-effort side write: rolled back alone, never
        // failing the review it rides on.
        Assert.True(result.Success);
        Assert.Equal("kept", (await scope.Games.GetAsync(gameId))!.ReviewNotes);
        var practice = Assert.Single(await scope.Objectives.GetGameObjectivesAsync(gameId));
        Assert.True(practice.Practiced);
        Assert.Null(practice.CriteriaMet);
    }

    [Fact]
    public async Task DeleteAsync_LateWriteFails_KeepsTheWholeReview()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 9105;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));
        var tagId = (await scope.ConceptTags.GetAllAsync()).First().Id;
        var workflow = CreateWorkflow(scope);
        Assert.True((await workflow.SaveAsync(new SaveReviewRequest(gameId, "Ahri", true, false,
            Snapshot with { ReviewNotes = "keep me", ImprovementNote = "note", SelectedTagIds = [tagId] }))).Success);
        await ExecAsync(scope, """
            CREATE TRIGGER fail_note_delete BEFORE DELETE ON matchup_notes
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);
        await scope.MatchupNotes.CreateAsync("Ahri", "Syndra", "lane note", gameId: gameId);

        var result = await workflow.DeleteAsync(gameId);

        Assert.False(result.Success);
        Assert.Equal("keep me", (await scope.Games.GetAsync(gameId))!.ReviewNotes);
        Assert.Equal("note", (await scope.SessionLog.GetEntryAsync(gameId))!.ImprovementNote);
        Assert.Equal([tagId], await scope.ConceptTags.GetIdsForGameAsync(gameId));
    }

    [Fact]
    public async Task WriteSession_DisposedWithoutCommit_DiscardsWrites()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(9106));

        using (var session = DbWriteSession.Begin(scope.ConnectionFactory))
        {
            await scope.Games.UpdateReviewAsync(9106, new GameReview { Rating = 1, Notes = "uncommitted" }, session);
        }

        Assert.Equal("", (await scope.Games.GetAsync(9106))!.ReviewNotes);
    }

    private static async Task ExecAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static ReviewWorkflowService CreateWorkflow(TestDatabaseScope scope) => new(
        scope.ConnectionFactory,
        scope.Games,
        scope.ConceptTags,
        scope.Vod,
        scope.SessionLog,
        scope.Objectives,
        scope.ReviewDrafts,
        scope.MatchupNotes,
        scope.Evidence,
        new TestConfigService(),
        NullLogger<ReviewWorkflowService>.Instance);

    private static readonly ReviewSnapshot Snapshot = new(
        MentalRating: 6,
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

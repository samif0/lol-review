using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// v2.15.0: exercises CRUD on objective_prompts + prompt_answers,
/// the phase+status filtering on GetActivePromptsForPhaseAsync,
/// and the pre_game_draft_prompts staging flow.
/// </summary>
public sealed class PromptsRepositoryTests
{
    // ── Prompt CRUD ─────────────────────────────────────────────────

    [Fact]
    public async Task CreatePromptAsync_PersistsAndIsReadable()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "2v2 planning", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        var promptId = await scope.Prompts.CreatePromptAsync(
            objId, ObjectivePhases.PreGame, "How do you think the 2v2 will go?", 0);

        var prompts = await scope.Prompts.GetPromptsForObjectiveAsync(objId);
        var prompt = Assert.Single(prompts);
        Assert.Equal(promptId, prompt.Id);
        Assert.Equal(objId, prompt.ObjectiveId);
        Assert.Equal(ObjectivePhases.PreGame, prompt.Phase);
        Assert.Equal("How do you think the 2v2 will go?", prompt.Label);
        Assert.Equal(0, prompt.SortOrder);
    }

    [Fact]
    public async Task UpdatePromptAsync_ChangesLabelPhaseAndOrder()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: true, practicePost: false);
        var promptId = await scope.Prompts.CreatePromptAsync(
            objId, ObjectivePhases.PreGame, "old label", 0);

        await scope.Prompts.UpdatePromptAsync(
            promptId, ObjectivePhases.InGame, "new label", 5);

        var prompts = await scope.Prompts.GetPromptsForObjectiveAsync(objId);
        var prompt = Assert.Single(prompts);
        Assert.Equal(ObjectivePhases.InGame, prompt.Phase);
        Assert.Equal("new label", prompt.Label);
        Assert.Equal(5, prompt.SortOrder);
    }

    [Fact]
    public async Task DeletePromptAsync_RemovesAnswersAndDrafts()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Yasuo", false);
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: true, practicePost: false);
        var promptId = await scope.Prompts.CreatePromptAsync(
            objId, ObjectivePhases.InGame, "question", 0);

        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "in-game answer");
        await scope.Prompts.SaveDraftAnswerAsync("lcu-session-123", promptId, "draft answer");

        await scope.Prompts.DeletePromptAsync(promptId);

        Assert.Empty(await scope.Prompts.GetPromptsForObjectiveAsync(objId));
        Assert.Empty(await scope.Prompts.GetAnswersForGameAsync(gameId));
        Assert.Empty(await scope.Prompts.GetDraftAnswersAsync("lcu-session-123"));
    }

    [Fact]
    public async Task GetPromptsForObjectiveAsync_OrdersBySortOrderThenId()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);
        var p1 = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.InGame, "first-created-last-sort", 10);
        var p2 = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.InGame, "second-created-first-sort", 0);
        var p3 = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.InGame, "third-created-mid-sort", 5);

        var prompts = await scope.Prompts.GetPromptsForObjectiveAsync(objId);

        Assert.Equal(3, prompts.Count);
        Assert.Equal(p2, prompts[0].Id); // sort_order 0
        Assert.Equal(p3, prompts[1].Id); // sort_order 5
        Assert.Equal(p1, prompts[2].Id); // sort_order 10
    }

    // ── GetActivePromptsForPhaseAsync ───────────────────────────────

    [Fact]
    public async Task GetActivePromptsForPhaseAsync_FiltersByPhase()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: true, practicePost: true);
        var prePrompt  = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame,  "pre q",  0);
        var inPrompt   = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.InGame,   "in q",   0);
        var postPrompt = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PostGame, "post q", 0);

        var preList  = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame);
        var inList   = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.InGame);
        var postList = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PostGame);

        Assert.Equal(prePrompt,  Assert.Single(preList).PromptId);
        Assert.Equal(inPrompt,   Assert.Single(inList).PromptId);
        Assert.Equal(postPrompt, Assert.Single(postList).PromptId);
    }

    [Fact]
    public async Task GetActivePromptsForPhaseAsync_RespectsParentPracticeBool()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Objective practices pre only; a postgame prompt exists on it but
        // should not render because the parent doesn't practice postgame.
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame,  "pre",  0);
        await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PostGame, "post", 0);

        var postList = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PostGame);
        Assert.Empty(postList);
    }

    [Fact]
    public async Task GetActivePromptsForPhaseAsync_ExcludesCompletedObjectives()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "q", 0);

        await scope.Objectives.MarkCompleteAsync(objId);

        var preList = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame);
        Assert.Empty(preList);
    }

    [Fact]
    public async Task GetActivePromptsForPhaseAsync_PriorityObjectivesSortFirst()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Second objective explicitly set as priority; its prompts should come
        // back first even though it was created after the non-priority one.
        var firstId = await scope.Objectives.CreateWithPhasesAsync(
            "First", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var priorityId = await scope.Objectives.CreateWithPhasesAsync(
            "Priority", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetPriorityAsync(priorityId);

        await scope.Prompts.CreatePromptAsync(firstId,    ObjectivePhases.PreGame, "first q",    0);
        await scope.Prompts.CreatePromptAsync(priorityId, ObjectivePhases.PreGame, "priority q", 0);

        var preList = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame);

        Assert.Equal(2, preList.Count);
        Assert.True(preList[0].IsPriority);
        Assert.Equal(priorityId, preList[0].ObjectiveId);
        Assert.False(preList[1].IsPriority);
    }

    [Fact]
    public async Task GetActivePromptsForPhaseAsync_WithinObjectiveUsesSortOrder()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "C", sortOrder: 2);
        await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "A", sortOrder: 0);
        await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "B", sortOrder: 1);

        var preList = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame);

        Assert.Equal(new[] { "A", "B", "C" }, preList.Select(p => p.Label).ToArray());
    }

    // ── Answers ─────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAnswerAsync_UpsertsOnConflict()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Lux", true);
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);
        var promptId = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.InGame, "Q", 0);

        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "first answer");
        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "second answer");

        var answers = await scope.Prompts.GetAnswersForGameAsync(gameId);
        var answer = Assert.Single(answers);
        Assert.Equal("second answer", answer.AnswerText);
    }

    [Fact]
    public async Task SaveAnswerAsync_EmptyTextDeletesRow()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Jinx", false);
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: false, practiceIn: false, practicePost: true);
        var promptId = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PostGame, "Q", 0);

        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "populated");
        Assert.Single(await scope.Prompts.GetAnswersForGameAsync(gameId));

        // Clearing the field should remove the row so it doesn't render empty.
        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "");
        Assert.Empty(await scope.Prompts.GetAnswersForGameAsync(gameId));

        // Whitespace-only also counts as empty.
        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "   \n  ");
        Assert.Empty(await scope.Prompts.GetAnswersForGameAsync(gameId));
    }

    [Fact]
    public async Task GetAnswersForGameAsync_JoinsWithPromptAndObjectiveMetadata()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Orianna", true);
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Wave control", "macro", "primary", "slow push at 4:00", "",
            practicePre: false, practiceIn: true, practicePost: true);
        await scope.Objectives.SetPriorityAsync(objId);
        var promptId = await scope.Prompts.CreatePromptAsync(
            objId, ObjectivePhases.PostGame, "Did you control waves?", 0);

        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "yes, mostly");

        var answers = await scope.Prompts.GetAnswersForGameAsync(gameId);
        var answer = Assert.Single(answers);
        Assert.Equal(promptId, answer.PromptId);
        Assert.Equal(objId, answer.ObjectiveId);
        Assert.Equal("Wave control", answer.ObjectiveTitle);
        Assert.True(answer.IsPriority);
        Assert.Equal(ObjectivePhases.PostGame, answer.Phase);
        Assert.Equal("Did you control waves?", answer.Label);
        Assert.Equal("yes, mostly", answer.AnswerText);
    }

    // ── Pre-game drafts ─────────────────────────────────────────────

    [Fact]
    public async Task PreGameDrafts_UpsertAndRead()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Pre obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var promptId = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "Plan?", 0);

        await scope.Prompts.SaveDraftAnswerAsync("sess-A", promptId, "initial plan");
        await scope.Prompts.SaveDraftAnswerAsync("sess-A", promptId, "revised plan");

        var drafts = await scope.Prompts.GetDraftAnswersAsync("sess-A");
        var draft = Assert.Single(drafts);
        Assert.Equal(promptId, draft.PromptId);
        Assert.Equal("revised plan", draft.AnswerText);
    }

    [Fact]
    public async Task PreGameDrafts_ScopedBySession()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var promptId = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "Q", 0);

        await scope.Prompts.SaveDraftAnswerAsync("sess-A", promptId, "A's answer");
        await scope.Prompts.SaveDraftAnswerAsync("sess-B", promptId, "B's answer");

        var a = await scope.Prompts.GetDraftAnswersAsync("sess-A");
        var b = await scope.Prompts.GetDraftAnswersAsync("sess-B");

        Assert.Equal("A's answer", Assert.Single(a).AnswerText);
        Assert.Equal("B's answer", Assert.Single(b).AnswerText);
    }

    [Fact]
    public async Task PromotePreGameDraftsAsync_CopiesToPromptAnswersAndClearsDrafts()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var promptId = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "Q", 0);

        await scope.Prompts.SaveDraftAnswerAsync("sess-X", promptId, "the answer");
        await scope.Prompts.PromotePreGameDraftsAsync("sess-X", gameId);

        // Answer copied across to real prompt_answers row
        var answers = await scope.Prompts.GetAnswersForGameAsync(gameId);
        Assert.Equal("the answer", Assert.Single(answers).AnswerText);

        // Drafts cleared
        Assert.Empty(await scope.Prompts.GetDraftAnswersAsync("sess-X"));
    }

    [Fact]
    public async Task PromotePreGameDraftsAsync_SkipsEmptyAnswers()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var populatedPrompt = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "populated", 0);
        var emptyPrompt = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "empty", 1);

        await scope.Prompts.SaveDraftAnswerAsync("sess-X", populatedPrompt, "got something");
        // Directly insert an empty draft to test the skip behavior (SaveDraftAnswerAsync deletes empties)
        using (var conn = scope.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pre_game_draft_prompts (session_key, prompt_id, answer_text, updated_at)
                VALUES ('sess-X', @promptId, '   ', 0)
                """;
            cmd.Parameters.AddWithValue("@promptId", emptyPrompt);
            await cmd.ExecuteNonQueryAsync();
        }

        await scope.Prompts.PromotePreGameDraftsAsync("sess-X", gameId);

        var answers = await scope.Prompts.GetAnswersForGameAsync(gameId);
        var answer = Assert.Single(answers);
        Assert.Equal(populatedPrompt, answer.PromptId);
    }

    [Fact]
    public async Task PromotePreGameDraftsAsync_IdempotentOnSecondCall()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var promptId = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.PreGame, "Q", 0);

        await scope.Prompts.SaveDraftAnswerAsync("sess-X", promptId, "value");
        await scope.Prompts.PromotePreGameDraftsAsync("sess-X", gameId);
        // Second promote on the now-empty draft set should no-op, not throw.
        await scope.Prompts.PromotePreGameDraftsAsync("sess-X", gameId);

        var answers = await scope.Prompts.GetAnswersForGameAsync(gameId);
        Assert.Equal("value", Assert.Single(answers).AnswerText);
    }
}

using Revu.Core.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace Revu.Core.Tests;

/// <summary>
/// v2.15.0: exercises the new multi-phase practice bools on objectives and
/// the schema migration that backfills them from the legacy <c>phase</c> column.
/// </summary>
public sealed class ObjectivesRepositoryPhaseTests
{
    [Fact]
    public async Task CreateWithPhasesAsync_PersistsAllThreeBools()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "2v2 planning",
            skillArea: "macro",
            type: "primary",
            completionCriteria: "Plan every 2v2",
            description: "",
            practicePre: true,
            practiceIn: true,
            practicePost: false);

        var obj = await scope.Objectives.GetAsync(id);
        Assert.NotNull(obj);
        Assert.True(obj!.PracticePre);
        Assert.True(obj.PracticeIn);
        Assert.False(obj.PracticePost);
    }

    [Fact]
    public async Task CreateWithPhasesAsync_SetsLegacyPhaseColumnToFirstTrueBool()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // pre=false, in=false, post=true → legacy phase must be "postgame"
        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Review matchup notes",
            "micro", "primary", "", "",
            practicePre: false, practiceIn: false, practicePost: true);

        var obj = await scope.Objectives.GetAsync(id);
        Assert.NotNull(obj);
        Assert.Equal(ObjectivePhases.PostGame, obj!.Phase);
    }

    [Fact]
    public async Task LegacyCreateAsync_MapsPhaseStringToMatchingBool()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Old-path CreateAsync(phase: "pregame") should set practice_pregame=1
        // and leave the other two false — backwards compat with unmigrated callers.
        var id = await scope.Objectives.CreateAsync(
            "Pre-game intention",
            phase: ObjectivePhases.PreGame);

        var obj = await scope.Objectives.GetAsync(id);
        Assert.NotNull(obj);
        Assert.True(obj!.PracticePre);
        Assert.False(obj.PracticeIn);
        Assert.False(obj.PracticePost);
    }

    [Fact]
    public async Task UpdateWithPhasesAsync_ReplacesAllThreeBools()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Objectives.UpdateWithPhasesAsync(
            id, "Obj", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: true);

        var obj = await scope.Objectives.GetAsync(id);
        Assert.False(obj!.PracticePre);
        Assert.True(obj.PracticeIn);
        Assert.True(obj.PracticePost);
    }

    [Fact]
    public async Task UpdatePracticePhasesAsync_LeavesOtherFieldsIntact()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Ward river at 3:00",
            skillArea: "macro",
            type: "primary",
            completionCriteria: "ward placed by 3:00 every game",
            description: "vision timing",
            practicePre: false, practiceIn: true, practicePost: false);

        await scope.Objectives.UpdatePracticePhasesAsync(id,
            practicePre: true, practiceIn: true, practicePost: true);

        var obj = await scope.Objectives.GetAsync(id);
        Assert.NotNull(obj);
        Assert.Equal("Ward river at 3:00", obj!.Title);
        Assert.Equal("macro", obj.SkillArea);
        Assert.Equal("ward placed by 3:00 every game", obj.CompletionCriteria);
        Assert.Equal("vision timing", obj.Description);
        Assert.True(obj.PracticePre);
        Assert.True(obj.PracticeIn);
        Assert.True(obj.PracticePost);
    }

    [Fact]
    public async Task GetActiveByPhaseAsync_OnlyReturnsObjectivesWithPhaseBoolSet()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var preOnly = await scope.Objectives.CreateWithPhasesAsync(
            "Pre-only", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var allThree = await scope.Objectives.CreateWithPhasesAsync(
            "All three", "", "primary", "", "",
            practicePre: true, practiceIn: true, practicePost: true);
        var inPostOnly = await scope.Objectives.CreateWithPhasesAsync(
            "In+Post", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: true);

        var preList = await scope.Objectives.GetActiveByPhaseAsync(ObjectivePhases.PreGame);
        var inList = await scope.Objectives.GetActiveByPhaseAsync(ObjectivePhases.InGame);
        var postList = await scope.Objectives.GetActiveByPhaseAsync(ObjectivePhases.PostGame);

        Assert.Equal(new[] { preOnly, allThree }.OrderBy(i => i), preList.Select(o => o.Id).OrderBy(i => i));
        Assert.Equal(new[] { allThree, inPostOnly }.OrderBy(i => i), inList.Select(o => o.Id).OrderBy(i => i));
        Assert.Equal(new[] { allThree, inPostOnly }.OrderBy(i => i), postList.Select(o => o.Id).OrderBy(i => i));
    }

    [Fact]
    public async Task GetActiveByPhaseAsync_ExcludesCompletedObjectives()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var activeId = await scope.Objectives.CreateWithPhasesAsync(
            "Active", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var completedId = await scope.Objectives.CreateWithPhasesAsync(
            "Completed", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.MarkCompleteAsync(completedId);

        var preList = await scope.Objectives.GetActiveByPhaseAsync(ObjectivePhases.PreGame);
        Assert.Single(preList);
        Assert.Equal(activeId, preList[0].Id);
    }

    [Fact]
    public async Task Migration_BackfillsPracticeBoolsFromLegacyPhaseColumn()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Seed legacy-shape rows: manually zero out the new bools and set only
        // the legacy phase column. Mimics a DB created on v2.14 and upgraded.
        using (var conn = scope.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO objectives
                    (title, skill_area, type, phase, completion_criteria, description,
                     status, is_priority, score, game_count, created_at,
                     practice_pregame, practice_ingame, practice_postgame)
                VALUES ('pre-legacy',  '', 'primary', 'pregame',  '', '', 'active', 0, 0, 0, 0, 0, 0, 0),
                       ('in-legacy',   '', 'primary', 'ingame',   '', '', 'active', 0, 0, 0, 0, 0, 0, 0),
                       ('post-legacy', '', 'primary', 'postgame', '', '', 'active', 0, 0, 0, 0, 0, 0, 0)
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Re-run initialize — backfill should run and set each bool.
        await scope.InitializeAsync();

        var all = await scope.Objectives.GetAllAsync();
        var pre  = all.Single(o => o.Title == "pre-legacy");
        var ing  = all.Single(o => o.Title == "in-legacy");
        var post = all.Single(o => o.Title == "post-legacy");

        Assert.True(pre.PracticePre);   Assert.False(pre.PracticeIn);   Assert.False(pre.PracticePost);
        Assert.False(ing.PracticePre);  Assert.True(ing.PracticeIn);    Assert.False(ing.PracticePost);
        Assert.False(post.PracticePre); Assert.False(post.PracticeIn);  Assert.True(post.PracticePost);
    }

    [Fact]
    public async Task Migration_BackfillIsIdempotent_DoesNotClobberExplicitMultiPhase()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // User explicitly configures an objective as {pre, post} (multi-phase).
        // The backfill's WHERE all-zero guard must protect this from being
        // overwritten by a later re-init.
        var id = await scope.Objectives.CreateWithPhasesAsync(
            "multi-phase", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: true);

        await scope.InitializeAsync();
        await scope.InitializeAsync();

        var obj = await scope.Objectives.GetAsync(id);
        Assert.True(obj!.PracticePre);
        Assert.False(obj.PracticeIn);
        Assert.True(obj.PracticePost);
    }

    [Fact]
    public async Task DeleteAsync_CascadesToObjectivePromptsAndAnswers()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Obj with prompts", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);
        var promptId = await scope.Prompts.CreatePromptAsync(objId, ObjectivePhases.InGame, "Did you execute?", 0);
        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "yes");

        await scope.Objectives.DeleteAsync(objId);

        // Prompts gone
        var remainingPrompts = await scope.Prompts.GetPromptsForObjectiveAsync(objId);
        Assert.Empty(remainingPrompts);

        // Answers gone
        var remainingAnswers = await scope.Prompts.GetAnswersForGameAsync(gameId);
        Assert.Empty(remainingAnswers);

        // Objective gone
        var gone = await scope.Objectives.GetAsync(objId);
        Assert.Null(gone);
    }
}

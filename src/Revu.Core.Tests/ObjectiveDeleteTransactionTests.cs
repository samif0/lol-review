namespace Revu.Core.Tests;

/// <summary>
/// Objective delete is one transaction (a failure partway keeps the player's prompt
/// answers with the objective) and leaves no dangling references behind: clips and
/// evidence stay, untagged, and pre-game prompt drafts go with their prompts.
/// </summary>
public sealed class ObjectiveDeleteTransactionTests
{
    [Fact]
    public async Task Delete_FailsPartway_KeepsPromptsAndAnswers()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8101));
        var objectiveId = await scope.Objectives.CreateAsync("Ward river", "vision");
        var promptId = await scope.Prompts.CreatePromptAsync(objectiveId, "ingame", "Where was the jungler?", 0);
        await scope.Prompts.SaveAnswerAsync(promptId, 8101, "Topside at 3:10");
        // The objectives row is deleted last; make that delete fail.
        await ExecAsync(scope, """
            CREATE TRIGGER fail_objective_delete BEFORE DELETE ON objectives
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => scope.Objectives.DeleteAsync(objectiveId));

        Assert.NotNull(await scope.Objectives.GetAsync(objectiveId));
        Assert.Single(await scope.Prompts.GetPromptsForObjectiveAsync(objectiveId));
        Assert.Equal(1, await CountAsync(scope, $"SELECT COUNT(*) FROM prompt_answers WHERE prompt_id = {promptId}"));
    }

    [Fact]
    public async Task Delete_DetachesClipsAndEvidence_AndDropsPromptDrafts()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8102));
        var objectiveId = await scope.Objectives.CreateAsync("Ward river", "vision");
        var keptId = await scope.Objectives.CreateAsync("Farm", "lane");
        var promptId = await scope.Prompts.CreatePromptAsync(objectiveId, "pregame", "Plan", 0);
        await ExecAsync(scope, $"""
            INSERT INTO vod_bookmarks (game_id, game_time_s, note, objective_id, prompt_id) VALUES (8102, 60, 'clip', {objectiveId}, {promptId});
            INSERT INTO vod_bookmarks (game_id, game_time_s, note, objective_id) VALUES (8102, 90, 'other', {keptId});
            INSERT INTO evidence_items (game_id, source_kind, objective_id, prompt_id) VALUES (8102, 'clip', {objectiveId}, {promptId});
            INSERT INTO pre_game_draft_prompts (session_key, prompt_id, answer_text) VALUES ('s1', {promptId}, 'draft');
            """);

        await scope.Objectives.DeleteAsync(objectiveId);

        Assert.Null(await scope.Objectives.GetAsync(objectiveId));
        Assert.Equal(2, await CountAsync(scope, "SELECT COUNT(*) FROM vod_bookmarks WHERE game_id = 8102"));
        Assert.Equal(0, await CountAsync(scope, $"SELECT COUNT(*) FROM vod_bookmarks WHERE objective_id = {objectiveId} OR prompt_id = {promptId}"));
        Assert.Equal(1, await CountAsync(scope, $"SELECT COUNT(*) FROM vod_bookmarks WHERE objective_id = {keptId}"));
        Assert.Equal(1, await CountAsync(scope, "SELECT COUNT(*) FROM evidence_items WHERE game_id = 8102 AND objective_id IS NULL AND prompt_id IS NULL"));
        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM pre_game_draft_prompts"));
    }

    private static async Task<long> CountAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

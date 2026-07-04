using Microsoft.Data.Sqlite;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// P-027 (schema v8): evidence_items.prompt_id lets a clip/auto-moment answer a
/// specific custom prompt so the review can group clips under their prompt. These
/// pin (1) the prompt_id round-trip through Upsert→read, and (2) the reachability
/// guard — deleting a prompt must NULL the dangling evidence link (so the clip
/// falls back to its objective / the "To sort" strip instead of vanishing), while
/// keeping the evidence row and its objective_id intact.
/// </summary>
public sealed class EvidencePromptIdTests
{
    [Fact]
    public async Task Upsert_RoundTripsPromptId()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        await InsertGameAsync(conn, gameId: 8001);
        var objectiveId = await scope.Objectives.CreateAsync(
            "Track jungle before pushing", phase: ObjectivePhases.InGame);
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Where was the jungler?", sortOrder: 0);

        var id = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: 8001, SourceKind: EvidenceKinds.Clip, SourceId: 555, SourceKey: "clip-555",
            StartTimeSeconds: 600, EndTimeSeconds: 612, Title: "Caught while pushing",
            ObjectiveId: objectiveId, PromptId: promptId));

        var rows = await scope.Evidence.GetForGameAsync(8001);
        var row = Assert.Single(rows, r => r.Id == id);
        Assert.Equal(promptId, row.PromptId);
        Assert.Equal(objectiveId, row.ObjectiveId);
    }

    [Fact]
    public async Task DeletePrompt_NullsEvidencePromptId_ButKeepsRowAndObjective()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        await InsertGameAsync(conn, gameId: 8002);
        var objectiveId = await scope.Objectives.CreateAsync(
            "Ward river by 2:45", phase: ObjectivePhases.InGame);
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Did I ward the entrance?", sortOrder: 0);

        var id = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: 8002, SourceKind: EvidenceKinds.Clip, SourceId: 777, SourceKey: "clip-777",
            StartTimeSeconds: 165, EndTimeSeconds: 175, Title: "No ward, ganked",
            ObjectiveId: objectiveId, PromptId: promptId));

        // Deleting the prompt must not orphan the clip under a now-missing prompt.
        await scope.Prompts.DeletePromptAsync(promptId);

        var rows = await scope.Evidence.GetForGameAsync(8002);
        var row = Assert.Single(rows, r => r.Id == id);
        Assert.Null(row.PromptId);            // dangling prompt link cleared
        Assert.Equal(objectiveId, row.ObjectiveId); // objective link preserved
    }

    private static async Task InsertGameAsync(SqliteConnection conn, long gameId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (game_id, champion_name, win, timestamp, queue_type, is_hidden)
            VALUES (@gameId, 'Jinx', 1, @timestamp, 'Ranked Solo/Duo', 0)";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@timestamp", DateTimeOffset.Now.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }
}

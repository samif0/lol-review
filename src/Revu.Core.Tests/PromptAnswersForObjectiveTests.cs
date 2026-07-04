using Microsoft.Data.Sqlite;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// P-024: the Objective Notes page (GET /api/objective/notes) used to surface
/// only the per-game execution_note and never read prompt_answers, so every
/// custom-prompt answer the user typed was invisible. These pin the new
/// per-objective aggregation read (PromptsRepository.GetAnswersForObjectiveAsync)
/// that the snapshot builder now consumes: it must return EVERY answer across
/// every prompt + game for the objective, carrying the game header fields.
/// </summary>
public sealed class PromptAnswersForObjectiveTests
{
    [Fact]
    public async Task GetAnswersForObjective_ReturnsAllAnswersAcrossPromptsAndGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var objectiveId = await scope.Objectives.CreateAsync(
            "Improve wave management", phase: ObjectivePhases.InGame);

        var promptA = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.PreGame, "What was my wave plan?", sortOrder: 0);
        var promptB = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.PostGame, "Did I lose plates I shouldn't have?", sortOrder: 1);

        await InsertGameAsync(conn, gameId: 7001, champion: "Aatrox", win: true);
        await InsertGameAsync(conn, gameId: 7002, champion: "Camille", win: false);

        await scope.Prompts.SaveAnswerAsync(promptA, 7001, "Slow push then freeze after recall.");
        await scope.Prompts.SaveAnswerAsync(promptA, 7002, "Freeze near tower, deny the all-in.");
        await scope.Prompts.SaveAnswerAsync(promptB, 7002, "Lost two plates after shoving.");

        var answers = await scope.Prompts.GetAnswersForObjectiveAsync(objectiveId);

        // All THREE answers must come back (not just one execution_note).
        Assert.Equal(3, answers.Count);

        // Both prompts represented.
        Assert.Equal(2, answers.Select(a => a.PromptId).Distinct().Count());
        Assert.Contains(answers, a => a.PromptId == promptA);
        Assert.Contains(answers, a => a.PromptId == promptB);

        // Answer text + game header fields are carried through.
        var aatrox = Assert.Single(answers, a => a.PromptId == promptA && a.GameId == 7001);
        Assert.Equal("Slow push then freeze after recall.", aatrox.AnswerText);
        Assert.Equal("Aatrox", aatrox.ChampionName);
        Assert.True(aatrox.Win);
        Assert.Equal("What was my wave plan?", aatrox.Label);
        Assert.Equal(ObjectivePhases.PreGame, aatrox.Phase);

        var camilleLoss = Assert.Single(answers, a => a.PromptId == promptB && a.GameId == 7002);
        Assert.Equal("Lost two plates after shoving.", camilleLoss.AnswerText);
        Assert.Equal("Camille", camilleLoss.ChampionName);
        Assert.False(camilleLoss.Win);
        Assert.Equal(ObjectivePhases.PostGame, camilleLoss.Phase);
    }

    [Fact]
    public async Task GetAnswersForObjective_ExcludesHiddenGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var objectiveId = await scope.Objectives.CreateAsync("Track jungle", phase: ObjectivePhases.InGame);
        var prompt = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Where was the enemy jungler?", sortOrder: 0);

        await InsertGameAsync(conn, gameId: 7101, champion: "Jinx", win: true);
        await InsertGameAsync(conn, gameId: 7102, champion: "Ezreal", win: false, isHidden: true);

        await scope.Prompts.SaveAnswerAsync(prompt, 7101, "Tracked top-side after first base.");
        await scope.Prompts.SaveAnswerAsync(prompt, 7102, "Should be hidden.");

        var answers = await scope.Prompts.GetAnswersForObjectiveAsync(objectiveId);

        var only = Assert.Single(answers);
        Assert.Equal(7101, only.GameId);
        Assert.Equal("Tracked top-side after first base.", only.AnswerText);
    }

    private static async Task InsertGameAsync(
        SqliteConnection conn, long gameId, string champion, bool win, bool isHidden = false)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (game_id, champion_name, win, timestamp, queue_type, is_hidden)
            VALUES (@gameId, @champion, @win, @timestamp, 'Ranked Solo/Duo', @isHidden)";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@champion", champion);
        cmd.Parameters.AddWithValue("@win", win ? 1 : 0);
        cmd.Parameters.AddWithValue("@timestamp", DateTimeOffset.Now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@isHidden", isHidden ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }
}

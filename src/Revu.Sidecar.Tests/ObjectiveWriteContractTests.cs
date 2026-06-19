#nullable enable

using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Objective WRITE contracts for the sidecar's set_*_objective + the save_review
/// objective-practice path.
///
/// The sidecar's per-objective "practiced / outcome" endpoints and save_review's
/// objective loop both land on <see cref="ObjectivesRepository.RecordGameAsync"/>
/// (save_review goes through <see cref="ReviewWorkflowService.SaveAsync"/>, which
/// iterates the snapshot's ObjectivePractices and calls RecordGameAsync per item).
/// These tests pin the load-bearing invariants of that seam:
///   * practicing an objective on a game persists into game_objectives;
///   * game_count increments ONCE per linked game, NOT once per practice toggle;
///   * re-reading returns the recorded state (practiced + execution note);
///   * a null/absent objective-practice list on save_review does NOT corrupt or
///     drop the existing game_objectives links.
/// </summary>
public sealed class ObjectiveWriteContractTests
{
    // ── RecordGameAsync: the set_*_objective + save_review practice seam ──────

    // Was [Fact(Skip=...)] for the g.mental_rating crash this test caught — FIXED in
    // ObjectivesRepository.GetGamesForObjectiveAsync (now LEFT JOIN session_log sl +
    // sl.mental_rating). Unskipped: this test now proves the fix (fresh-install DBs
    // no longer throw "no such column: g.mental_rating").
    [Fact]
    public async Task RecordGameAsync_PracticedTrue_PersistsLinkAndIsReReadable()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2001, win: true);
        var objId = await scope.Objectives.CreateAsync("Ward river by 3:00", phase: ObjectivePhases.InGame);

        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: true, executionNote: "warded at 2:50");

        // Re-read via the game-keyed projection the review screen uses.
        var perGame = await scope.Objectives.GetGameObjectivesAsync(game.GameId);
        var record = Assert.Single(perGame);
        Assert.Equal(objId, record.ObjectiveId);
        Assert.True(record.Practiced);
        Assert.Equal("warded at 2:50", record.ExecutionNote);

        // Re-read via the objective-keyed projection the objective detail uses.
        // NOTE: this line is what trips the g.mental_rating product bug above.
        var perObjective = await scope.Objectives.GetGamesForObjectiveAsync(objId);
        var entry = Assert.Single(perObjective);
        Assert.Equal(game.GameId, entry.GameId);
        Assert.True(entry.Practiced);
        Assert.Equal("warded at 2:50", entry.ExecutionNote);
    }

    [Fact]
    public async Task RecordGameAsync_PracticedTrue_AwardsScoreAndCountsGameOnce()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2002);
        var objId = await scope.Objectives.CreateAsync("CS focus", phase: ObjectivePhases.InGame);

        var before = await scope.Objectives.GetAsync(objId);
        Assert.NotNull(before);
        Assert.Equal(0, before!.Score);
        Assert.Equal(0, before.GameCount);

        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: true);

        var after = await scope.Objectives.GetAsync(objId);
        Assert.NotNull(after);
        // Practiced contributes +2 to score, and the first link bumps game_count by 1.
        Assert.Equal(2, after!.Score);
        Assert.Equal(1, after.GameCount);
    }

    [Fact]
    public async Task RecordGameAsync_PracticedFalse_LinksGameWithoutScoreButStillCountsGame()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2003);
        var objId = await scope.Objectives.CreateAsync("Tempo", phase: ObjectivePhases.InGame);

        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: false);

        var after = await scope.Objectives.GetAsync(objId);
        Assert.NotNull(after);
        // Unpracticed: no score award, but the game IS linked so game_count still
        // counts it once (game_count = "games this objective was carried into").
        Assert.Equal(0, after!.Score);
        Assert.Equal(1, after.GameCount);

        var perGame = await scope.Objectives.GetGameObjectivesAsync(game.GameId);
        var record = Assert.Single(perGame);
        Assert.False(record.Practiced);
    }

    [Fact]
    public async Task RecordGameAsync_SameGameTwice_IncrementsGameCountOnlyOnce()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2004);
        var objId = await scope.Objectives.CreateAsync("Roam timing", phase: ObjectivePhases.InGame);

        // Record the same (game, objective) link twice — e.g. the user toggles
        // the practiced flag, or save_review runs again. game_count must NOT
        // double-count: it is per-linked-game, not per-write.
        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: false);
        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: true);

        var after = await scope.Objectives.GetAsync(objId);
        Assert.NotNull(after);
        Assert.Equal(1, after!.GameCount); // counted exactly once across both writes
        // false→true: score goes 0 → +2 (delta is the difference, not additive).
        Assert.Equal(2, after.Score);

        // Only one game_objectives row exists for the pair (INSERT OR REPLACE upsert).
        var perGame = await scope.Objectives.GetGameObjectivesAsync(game.GameId);
        var record = Assert.Single(perGame);
        Assert.True(record.Practiced);
    }

    [Fact]
    public async Task RecordGameAsync_TwoDistinctGames_IncrementsGameCountPerGame()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var g1 = await scope.SeedGameAsync(gameId: 2005);
        var g2 = await scope.SeedGameAsync(gameId: 2006);
        var objId = await scope.Objectives.CreateAsync("Vision control", phase: ObjectivePhases.InGame);

        await scope.Objectives.RecordGameAsync(g1.GameId, objId, practiced: true);
        await scope.Objectives.RecordGameAsync(g2.GameId, objId, practiced: true);

        var after = await scope.Objectives.GetAsync(objId);
        Assert.NotNull(after);
        Assert.Equal(2, after!.GameCount); // one per distinct linked game
        Assert.Equal(4, after.Score);      // +2 per practiced game
    }

    [Fact]
    public async Task RecordGameAsync_FlipPracticedTrueToFalse_RemovesAwardedScoreWithoutDoubleCounting()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2007);
        var objId = await scope.Objectives.CreateAsync("Wave management", phase: ObjectivePhases.InGame);

        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: true);
        var afterTrue = await scope.Objectives.GetAsync(objId);
        Assert.Equal(2, afterTrue!.Score);
        Assert.Equal(1, afterTrue.GameCount);

        // Flip back to unpracticed — score is clawed back, game still counted once.
        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: false);
        var afterFalse = await scope.Objectives.GetAsync(objId);
        Assert.Equal(0, afterFalse!.Score);
        Assert.Equal(1, afterFalse.GameCount);
    }

    [Fact]
    public async Task RecordGameAsync_ScoreNeverGoesNegative()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2008);
        var objId = await scope.Objectives.CreateAsync("Defensive itemization", phase: ObjectivePhases.InGame);

        // Practiced=false on a fresh objective applies score delta 0, clamped at 0.
        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: false);

        var after = await scope.Objectives.GetAsync(objId);
        Assert.Equal(0, after!.Score);
    }

    [Fact]
    public async Task RecordGameAsync_DefaultExecutionNote_PersistsAsEmptyString()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2009);
        var objId = await scope.Objectives.CreateAsync("Trade discipline", phase: ObjectivePhases.InGame);

        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: true);

        var perGame = await scope.Objectives.GetGameObjectivesAsync(game.GameId);
        var record = Assert.Single(perGame);
        Assert.Equal("", record.ExecutionNote);
    }

    // ── save_review path: ReviewWorkflowService.SaveAsync applies practices ───

    [Fact]
    public async Task SaveReview_AppliesObjectivePractice_PersistsLinkAndCountsGameOnce()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2010, champion: "Ahri", win: true);
        var objId = await scope.Objectives.CreateAsync("Cooldown tracking", phase: ObjectivePhases.PostGame);

        var request = MakeSaveRequest(game.GameId, "Ahri", win: true, practices: new[]
        {
            new SaveObjectivePracticeRequest(objId, Practiced: true, ExecutionNote: "tracked ult timers"),
        });

        var result = await scope.ReviewWorkflow.SaveAsync(request);
        Assert.True(result.Success, result.ErrorMessage);

        var perGame = await scope.Objectives.GetGameObjectivesAsync(game.GameId);
        var record = Assert.Single(perGame);
        Assert.Equal(objId, record.ObjectiveId);
        Assert.True(record.Practiced);
        Assert.Equal("tracked ult timers", record.ExecutionNote);

        var obj = await scope.Objectives.GetAsync(objId);
        Assert.Equal(1, obj!.GameCount);
        Assert.Equal(2, obj.Score);
    }

    [Fact]
    public async Task SaveReview_TwiceWithSamePractice_DoesNotDoubleCountGame()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2011, champion: "Ahri");
        var objId = await scope.Objectives.CreateAsync("Map awareness", phase: ObjectivePhases.PostGame);

        var practices = new[] { new SaveObjectivePracticeRequest(objId, Practiced: true, ExecutionNote: "") };

        // First save (user reviews), then a second save (user edits + re-saves).
        Assert.True((await scope.ReviewWorkflow.SaveAsync(MakeSaveRequest(game.GameId, "Ahri", true, practices))).Success);
        Assert.True((await scope.ReviewWorkflow.SaveAsync(MakeSaveRequest(game.GameId, "Ahri", true, practices))).Success);

        var obj = await scope.Objectives.GetAsync(objId);
        // game_count must stay at 1: the game is linked once, re-saving the review
        // is not a second game. Score also stays at +2 (no re-award).
        Assert.Equal(1, obj!.GameCount);
        Assert.Equal(2, obj.Score);
    }

    [Fact]
    public async Task SaveReview_EmptyPracticeList_DoesNotCorruptExistingLinks()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2012, champion: "Ahri");
        var objId = await scope.Objectives.CreateAsync("Objective setup", phase: ObjectivePhases.PostGame);

        // Establish an existing practiced link directly (as a prior set_objective would).
        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: true, executionNote: "set up before drake");
        var beforeObj = await scope.Objectives.GetAsync(objId);
        Assert.Equal(1, beforeObj!.GameCount);
        Assert.Equal(2, beforeObj.Score);

        // Now a save_review whose snapshot carries NO objective practices (the form
        // omitted them / the user didn't touch the objectives section). The save
        // path must leave the existing game_objectives row + score/count intact —
        // an absent list is "no change", never a wipe.
        var request = MakeSaveRequest(game.GameId, "Ahri", win: true,
            practices: Array.Empty<SaveObjectivePracticeRequest>());
        var result = await scope.ReviewWorkflow.SaveAsync(request);
        Assert.True(result.Success, result.ErrorMessage);

        var perGame = await scope.Objectives.GetGameObjectivesAsync(game.GameId);
        var record = Assert.Single(perGame);
        Assert.True(record.Practiced);
        Assert.Equal("set up before drake", record.ExecutionNote);

        var afterObj = await scope.Objectives.GetAsync(objId);
        Assert.Equal(1, afterObj!.GameCount); // untouched
        Assert.Equal(2, afterObj.Score);      // untouched
    }

    [Fact]
    public async Task SaveReview_PracticeListForMultipleObjectives_LinksEachExactlyOnce()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2013, champion: "Ahri");
        var objA = await scope.Objectives.CreateAsync("A", phase: ObjectivePhases.PostGame);
        var objB = await scope.Objectives.CreateAsync("B", phase: ObjectivePhases.PostGame);

        var request = MakeSaveRequest(game.GameId, "Ahri", win: true, practices: new[]
        {
            new SaveObjectivePracticeRequest(objA, Practiced: true, ExecutionNote: ""),
            new SaveObjectivePracticeRequest(objB, Practiced: false, ExecutionNote: ""),
        });

        Assert.True((await scope.ReviewWorkflow.SaveAsync(request)).Success);

        var a = await scope.Objectives.GetAsync(objA);
        var b = await scope.Objectives.GetAsync(objB);
        Assert.Equal(1, a!.GameCount);
        Assert.Equal(2, a.Score);      // practiced
        Assert.Equal(1, b!.GameCount);
        Assert.Equal(0, b.Score);      // not practiced, still linked once

        var perGame = await scope.Objectives.GetGameObjectivesAsync(game.GameId);
        Assert.Equal(2, perGame.Count);
    }

    [Fact]
    public async Task RecordGameAsync_RawRow_HasExpectedPracticedFlag()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 2014);
        var objId = await scope.Objectives.CreateAsync("Backline access", phase: ObjectivePhases.InGame);

        await scope.Objectives.RecordGameAsync(game.GameId, objId, practiced: true, executionNote: "flanked");

        // Raw-SQL assertion: exactly one game_objectives row, practiced = 1.
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), MAX(practiced), MAX(execution_note)
            FROM game_objectives
            WHERE game_id = @gameId AND objective_id = @objectiveId
            """;
        cmd.Parameters.AddWithValue("@gameId", game.GameId);
        cmd.Parameters.AddWithValue("@objectiveId", objId);
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));   // one row
        Assert.Equal(1, reader.GetInt32(1));   // practiced = 1
        Assert.Equal("flanked", reader.GetString(2));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal save_review request that mirrors what the Tauri review
    /// form sends: the null-able attribution/enemy fields are left null (omitted),
    /// only the objective-practice list is meaningful for these tests.
    /// </summary>
    private static SaveReviewRequest MakeSaveRequest(
        long gameId, string champion, bool win, IReadOnlyList<SaveObjectivePracticeRequest> practices)
    {
        var snapshot = new ReviewSnapshot(
            MentalRating: 5,
            WentWell: "ok",
            Mistakes: "",
            FocusNext: "",
            ReviewNotes: "",
            ImprovementNote: null,
            Attribution: "",
            MentalHandled: null,
            SpottedProblems: "",
            OutsideControl: null,
            WithinControl: null,
            PersonalContribution: null,
            EnemyLaner: null,
            MatchupNote: null,
            SelectedTagIds: Array.Empty<long>(),
            ObjectivePractices: practices,
            FocusAdherence: null);

        return new SaveReviewRequest(
            GameId: gameId,
            ChampionName: champion,
            Win: win,
            RequireReviewNotes: false,
            Snapshot: snapshot);
    }
}

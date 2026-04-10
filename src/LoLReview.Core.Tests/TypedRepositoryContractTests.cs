using LoLReview.Core.Models;
using LoLReview.Core.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Tests;

public sealed class TypedRepositoryContractTests
{
    [Fact]
    public async Task TypedRepositories_ReturnStructuredRowsForReviewData()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync(
            "Respect first reset",
            skillArea: "macro",
            completionCriteria: "Crash before base");

        await scope.Objectives.SetPriorityAsync(objectiveId);
        await scope.Objectives.RecordGameAsync(gameId, objectiveId, practiced: true, executionNote: "Held wave long enough");
        await scope.MatchupNotes.UpsertForGameAsync(gameId, "Ahri", "Syndra", "Play outside Q line");
        await scope.Vod.LinkVodAsync(gameId, @"C:\vods\ahri-win.mp4", 1234, 1810);
        await scope.Vod.AddBookmarkAsync(
            gameId,
            95,
            "Trade setup",
            clipStartSeconds: 90,
            clipEndSeconds: 102,
            clipPath: @"C:\clips\trade.mp4",
            quality: "bad");

        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 120, Details = """{"victim":"Syndra"}""" },
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 130, Details = """{"victim":"Lee Sin"}""" },
        };
        await scope.GameEvents.SaveEventsAsync(gameId, events);

        var definitionId = await scope.DerivedEvents.CreateAsync(
            "Back-to-back kills",
            [GameEvent.EventTypes.Kill],
            minCount: 2,
            windowSeconds: 15,
            color: "#ffaa00");
        var definitions = await scope.DerivedEvents.GetAllDefinitionsAsync();
        var savedEvents = await scope.GameEvents.GetEventsAsync(gameId);
        var instances = scope.DerivedEvents.ComputeInstances(gameId, savedEvents, definitions);
        await scope.DerivedEvents.SaveInstancesAsync(gameId, instances);

        var activeObjectives = await scope.Objectives.GetActiveAsync();
        var gameObjectives = await scope.Objectives.GetGameObjectivesAsync(gameId);
        var matchupNote = await scope.MatchupNotes.GetForGameAsync(gameId);
        var vod = await scope.Vod.GetVodAsync(gameId);
        var bookmarks = await scope.Vod.GetBookmarksAsync(gameId);
        var derivedInstances = await scope.DerivedEvents.GetInstancesAsync(gameId);

        var objective = Assert.Single(activeObjectives);
        Assert.Equal(objectiveId, objective.Id);
        Assert.Equal("Respect first reset", objective.Title);
        Assert.True(objective.IsPriority);
        Assert.Equal(ObjectivePhases.InGame, objective.Phase);

        var gameObjective = Assert.Single(gameObjectives);
        Assert.Equal(objectiveId, gameObjective.ObjectiveId);
        Assert.True(gameObjective.Practiced);
        Assert.Equal("Held wave long enough", gameObjective.ExecutionNote);

        Assert.NotNull(matchupNote);
        Assert.Equal("Syndra", matchupNote!.Enemy);
        Assert.Equal("Play outside Q line", matchupNote.Note);

        Assert.NotNull(vod);
        Assert.Equal(@"C:\vods\ahri-win.mp4", vod!.FilePath);
        Assert.Equal(1810, vod.DurationSeconds);

        var bookmark = Assert.Single(bookmarks);
        Assert.Equal(95, bookmark.GameTimeSeconds);
        Assert.Equal("Trade setup", bookmark.Note);
        Assert.Equal(@"C:\clips\trade.mp4", bookmark.ClipPath);
        Assert.Equal("bad", bookmark.Quality);

        Assert.Collection(
            savedEvents,
            gameEvent =>
            {
                Assert.Equal(GameEvent.EventTypes.Kill, gameEvent.EventType);
                Assert.Equal(120, gameEvent.GameTimeS);
            },
            gameEvent =>
            {
                Assert.Equal(GameEvent.EventTypes.Kill, gameEvent.EventType);
                Assert.Equal(130, gameEvent.GameTimeS);
            });

        var definition = Assert.Single(definitions, item => item.Id == definitionId);
        Assert.Equal("Back-to-back kills", definition.Name);
        Assert.Equal([GameEvent.EventTypes.Kill], definition.SourceTypes);

        var instance = Assert.Single(derivedInstances, item => item.DefinitionId == definitionId);
        Assert.Equal(definitionId, instance.DefinitionId);
        Assert.Equal(120, instance.StartTimeSeconds);
        Assert.Equal(130, instance.EndTimeSeconds);
        Assert.Equal("Back-to-back kills", instance.DefinitionName);
        Assert.Equal("#ffaa00", instance.Color);
    }

    [Fact]
    public async Task GameEventsRepository_RoundTrips64BitGameIds()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 5_531_387_189;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));

        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Dragon, GameTimeS = 334, Details = """{"dragon_type":"Chemtech"}""" },
        };

        await scope.GameEvents.SaveEventsAsync(gameId, events);
        var savedEvents = await scope.GameEvents.GetEventsAsync(gameId);

        var savedEvent = Assert.Single(savedEvents);
        Assert.Equal(gameId, savedEvent.GameId);
        Assert.Equal(GameEvent.EventTypes.Dragon, savedEvent.EventType);
        Assert.Equal(334, savedEvent.GameTimeS);
    }

    [Fact]
    public async Task VodRepository_DeleteBookmarkAsync_RemovesSavedBookmarksAndClips()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Jinx", true);
        var noteBookmarkId = await scope.Vod.AddBookmarkAsync(gameId, 95, "Trade setup");
        var clipBookmarkId = await scope.Vod.AddBookmarkAsync(
            gameId,
            180,
            "Dragon fight",
            clipStartSeconds: 176,
            clipEndSeconds: 190,
            clipPath: @"C:\clips\dragon-fight.mp4");

        await scope.Vod.DeleteBookmarkAsync(noteBookmarkId);
        var remainingAfterFirstDelete = await scope.Vod.GetBookmarksAsync(gameId);

        var remainingBookmark = Assert.Single(remainingAfterFirstDelete);
        Assert.Equal(clipBookmarkId, remainingBookmark.Id);
        Assert.Equal(@"C:\clips\dragon-fight.mp4", remainingBookmark.ClipPath);

        await scope.Vod.DeleteBookmarkAsync(clipBookmarkId);
        var remainingAfterSecondDelete = await scope.Vod.GetBookmarksAsync(gameId);

        Assert.Empty(remainingAfterSecondDelete);
    }

    [Fact]
    public async Task VodRepository_UpdateBookmarkAsync_UpdatesClipQuality()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Kai'Sa", true);
        var bookmarkId = await scope.Vod.AddBookmarkAsync(
            gameId,
            144,
            "Hold wave before trade",
            clipStartSeconds: 140,
            clipEndSeconds: 150,
            clipPath: @"C:\clips\trade-window.mp4",
            quality: "neutral");

        await scope.Vod.UpdateBookmarkAsync(bookmarkId, quality: "bad");

        var bookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(gameId));
        Assert.Equal("bad", bookmark.Quality);
    }

    [Fact]
    public async Task VodRepository_DeleteBookmarkAsync_RemovesClipBackedCoachRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Jinx", true);
        var clipBookmarkId = await scope.Vod.AddBookmarkAsync(
            gameId,
            180,
            "Dragon fight",
            clipStartSeconds: 176,
            clipEndSeconds: 190,
            clipPath: @"C:\clips\dragon-fight.mp4");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using (var connection = scope.OpenConnection())
        {
            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_players (id, display_name, is_primary, created_at, updated_at)
                VALUES (1, 'Tester', 1, @now, @now)
                """,
                ("@now", now));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_moments (
                    id, player_id, game_id, bookmark_id, source_type, patch_version, champion, role, game_time_s,
                    clip_start_s, clip_end_s, clip_path, storyboard_path, hud_strip_path, minimap_strip_path, manifest_path,
                    note_text, context_text, dataset_version, model_version, created_at
                )
                VALUES (
                    501, 1, @gameId, @bookmarkId, 'manual_clip', 'unknown', 'Jinx', 'BOTTOM', 180,
                    176, 190, @clipPath, '', '', '', '', 'Dragon fight', '',
                    'bootstrap-v1', '', @now
                )
                """,
                ("@gameId", gameId),
                ("@bookmarkId", clipBookmarkId),
                ("@clipPath", @"C:\clips\dragon-fight.mp4"),
                ("@now", now));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_labels (
                    moment_id, player_id, label_quality, primary_reason, objective_key, explanation,
                    confidence, source, created_at, updated_at
                )
                VALUES (
                    501, 1, 'good', 'Held lane prio for dragon setup', 'dragon_setup', 'good setup',
                    0.8, 'manual', @now, @now
                )
                """,
                ("@now", now));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_inferences (
                    moment_id, player_id, model_version, inference_mode, moment_quality, primary_reason,
                    objective_key, confidence, rationale, raw_payload, created_at, updated_at
                )
                VALUES (
                    501, 1, 'gemma-e4b-base-1', 'gemma', 'good', 'Held lane prio for dragon setup',
                    'dragon_setup', 0.7, 'looked clean', '{}', @now, @now
                )
                """,
                ("@now", now));
        }

        await scope.Vod.DeleteBookmarkAsync(clipBookmarkId);

        await using var verificationConnection = scope.OpenConnection();
        var bookmarkCount = await ExecuteScalarAsync<long>(verificationConnection, """
            SELECT COUNT(*)
            FROM vod_bookmarks
            WHERE id = @bookmarkId
            """,
            ("@bookmarkId", clipBookmarkId));
        var momentCount = await ExecuteScalarAsync<long>(verificationConnection, """
            SELECT COUNT(*)
            FROM coach_moments
            WHERE bookmark_id = @bookmarkId
            """,
            ("@bookmarkId", clipBookmarkId));
        var labelCount = await ExecuteScalarAsync<long>(verificationConnection, """
            SELECT COUNT(*)
            FROM coach_labels
            WHERE moment_id = 501
            """);
        var inferenceCount = await ExecuteScalarAsync<long>(verificationConnection, """
            SELECT COUNT(*)
            FROM coach_inferences
            WHERE moment_id = 501
            """);

        Assert.Equal(0, bookmarkCount);
        Assert.Equal(0, momentCount);
        Assert.Equal(0, labelCount);
        Assert.Equal(0, inferenceCount);
    }

    [Fact]
    public async Task ObjectivesRepository_UpdateAsync_PersistsEditableFields()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync(
            "Respect first reset",
            skillArea: "macro",
            completionCriteria: "Crash before base",
            description: "Initial notes",
            phase: ObjectivePhases.InGame);

        await scope.Objectives.UpdateAsync(
            objectiveId,
            "Review loading screen plan",
            skillArea: "prep",
            type: "mental",
            completionCriteria: "Name first 3 waves",
            description: "Updated notes",
            phase: ObjectivePhases.PreGame);

        var updated = await scope.Objectives.GetAsync(objectiveId);

        Assert.NotNull(updated);
        Assert.Equal("Review loading screen plan", updated!.Title);
        Assert.Equal("prep", updated.SkillArea);
        Assert.Equal("mental", updated.Type);
        Assert.Equal("Name first 3 waves", updated.CompletionCriteria);
        Assert.Equal("Updated notes", updated.Description);
        Assert.Equal(ObjectivePhases.PreGame, updated.Phase);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalarValue = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(scalarValue!, typeof(T));
    }
}

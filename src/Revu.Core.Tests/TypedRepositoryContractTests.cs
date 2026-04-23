using Revu.Core.Models;
using Revu.Core.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace Revu.Core.Tests;

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

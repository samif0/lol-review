using LoLReview.Core.Models;

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
        await scope.Vod.AddBookmarkAsync(gameId, 95, "Trade setup", clipStartSeconds: 90, clipEndSeconds: 102, clipPath: @"C:\clips\trade.mp4");

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
}

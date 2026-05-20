namespace Revu.Core.Tests;

public sealed class RepositoryBatchReadTests
{
    [Fact]
    public async Task BatchObjectiveAndBookmarkReads_ReturnOnlyMatchingGameIds()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Track objective setup", "macro");
        const long practicedGameId = 900001;
        const long unpracticedGameId = 900002;
        const long untaggedGameId = 900003;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(practicedGameId, champion: "Ahri", win: true));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(unpracticedGameId, champion: "Kai'Sa", win: false));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(untaggedGameId, champion: "Lux", win: true));

        await scope.Objectives.RecordGameAsync(practicedGameId, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(unpracticedGameId, objectiveId, practiced: false);

        await scope.Vod.AddBookmarkAsync(practicedGameId, 900, "Dragon setup", objectiveId: objectiveId);
        await scope.Vod.AddBookmarkAsync(untaggedGameId, 600, "No tag");

        var ids = new[] { practicedGameId, unpracticedGameId, untaggedGameId };
        var practicedIds = await scope.Objectives.GetGamesWithPracticedObjectivesAsync(ids);
        var taggedBookmarkIds = await scope.Vod.GetGamesWithObjectiveTaggedBookmarksAsync(ids);

        Assert.Contains(practicedGameId, practicedIds);
        Assert.DoesNotContain(unpracticedGameId, practicedIds);
        Assert.DoesNotContain(untaggedGameId, practicedIds);

        Assert.Contains(practicedGameId, taggedBookmarkIds);
        Assert.DoesNotContain(unpracticedGameId, taggedBookmarkIds);
        Assert.DoesNotContain(untaggedGameId, taggedBookmarkIds);
    }
}

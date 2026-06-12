using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

public sealed class DeathClassificationsRepositoryTests
{
    [Fact]
    public async Task UpsertClassifyAndMix_RoundTrips()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var repo = new DeathClassificationsRepository(scope.ConnectionFactory);

        var gameId = await scope.Games.SaveManualAsync("Jinx", false);

        await repo.UpsertAsync(gameId, 312, DeathClasses.Vision);
        await repo.UpsertAsync(gameId, 845, DeathClasses.Vision);
        await repo.UpsertAsync(gameId, 1290, DeathClasses.Greed);

        var forGame = await repo.GetForGameAsync(gameId);
        Assert.Equal(3, forGame.Count);
        Assert.Equal(312, forGame[0].GameTimeSeconds);
        Assert.Equal(DeathClasses.Vision, forGame[0].DeathClass);

        // Re-classifying the same death updates in place (no duplicate row).
        await repo.UpsertAsync(gameId, 312, DeathClasses.Tempo);
        forGame = await repo.GetForGameAsync(gameId);
        Assert.Equal(3, forGame.Count);
        Assert.Equal(DeathClasses.Tempo, forGame[0].DeathClass);

        var mix = await repo.GetClassMixAsync(days: 14);
        Assert.Equal(3, mix.Sum(m => m.Count));
        // Vision (1), Greed (1), Tempo (1) — but ordering is by count desc then name.
        Assert.Equal(3, mix.Count);

        // Clearing removes the row entirely.
        await repo.ClearAsync(gameId, 845);
        forGame = await repo.GetForGameAsync(gameId);
        Assert.Equal(2, forGame.Count);
    }

    [Fact]
    public async Task ClassMix_ExcludesHiddenGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var repo = new DeathClassificationsRepository(scope.ConnectionFactory);

        // SaveManualAsync derives game_id from unix seconds, so two saves in
        // the same second collide — save via the ranked path with explicit ids.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var visibleId = await SaveRankedGameAsync(scope, gameId: 910_001, timestamp: now, "Ahri", win: true);
        var hiddenId = await SaveRankedGameAsync(scope, gameId: 910_002, timestamp: now, "Zed", win: false);
        await repo.UpsertAsync(visibleId, 100, DeathClasses.Wave);
        await repo.UpsertAsync(hiddenId, 200, DeathClasses.Wave);

        await scope.Games.SetHiddenAsync(hiddenId, hidden: true);

        var mix = await repo.GetClassMixAsync(days: 14);
        Assert.Equal(1, mix.Sum(m => m.Count));
    }

    private static async Task<long> SaveRankedGameAsync(
        TestDatabaseScope scope, long gameId, long timestamp, string champion, bool win)
    {
        var saved = await scope.Games.SaveAsync(new Revu.Core.Models.GameStats
        {
            GameId = gameId,
            Timestamp = timestamp,
            QueueType = "Ranked Solo/Duo",
            ChampionName = champion,
            Win = win,
            GameDuration = 1800,
        });
        Assert.True(saved >= 0);
        return gameId;
    }

    [Fact]
    public void DeathClasses_LabelsResolveForAllKeys()
    {
        foreach (var (key, label, hint) in DeathClasses.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.False(string.IsNullOrWhiteSpace(hint));
            Assert.Equal(label, DeathClasses.LabelFor(key));
        }

        Assert.Equal("", DeathClasses.LabelFor("nonsense"));
        Assert.Equal("", DeathClasses.LabelFor(null));
    }
}

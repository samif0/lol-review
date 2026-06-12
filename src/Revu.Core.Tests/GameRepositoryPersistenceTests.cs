using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class GameRepositoryPersistenceTests
{
    /// <summary>
    /// P-003 regression: StatsExtractor stamps participant_map at EOG so the
    /// 2v2 pairing renders immediately after game end, but SaveAsync silently
    /// dropped the column — it only appeared after EnemyLanerBackfillService
    /// rewrote the row. The insert must round-trip both matchup fields.
    /// </summary>
    [Fact]
    public async Task SaveAsync_PersistsEnemyLanerAndParticipantMap()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var stats = TestGameStatsFactory.Create(gameId: 4242, champion: "Kai'Sa");
        stats.Position = "BOTTOM";
        stats.EnemyLaner = "Tristana";
        stats.ParticipantMap =
            """{"ownBot":"Kai'Sa","ownSupp":"Nautilus","enemyBot":"Tristana","enemySupp":"Renata Glasc"}""";

        await scope.Games.SaveAsync(stats);

        var game = await scope.Games.GetAsync(4242);
        Assert.NotNull(game);
        Assert.Equal("Tristana", game!.EnemyLaner);
        Assert.Equal(stats.ParticipantMap, game.ParticipantMap);

        // End-to-end: the freshly inserted row alone must be enough for the
        // role-aware 2v2 headline — no backfill round-trip.
        Assert.Equal(
            "Kai'Sa+Nautilus vs Tristana+Renata Glasc",
            MatchupDisplay.Build(game.ChampionName, game.EnemyLaner, game.Position, game.ParticipantMap));
    }
}

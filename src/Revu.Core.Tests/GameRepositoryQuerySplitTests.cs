using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

public sealed class GameRepositoryQuerySplitTests
{
    [Fact]
    public async Task HistoryQuery_GetRecentAsync_AppliesChampionAndWinFilters()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameHistoryQuery history = scope.Games;

        await scope.Games.SaveAsync(TestGameStatsFactory.Create(1001, champion: "Ahri", win: true, timestamp: 1001));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(1002, champion: "Ahri", win: false, timestamp: 1002));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(1003, champion: "Lux", win: true, timestamp: 1003));

        var ahriWins = await history.GetRecentAsync(champion: "Ahri", win: true);

        var game = Assert.Single(ahriWins);
        Assert.Equal(1001, game.GameId);
    }

    [Fact]
    public async Task HistoryQuery_GetReviewedCountAsync_ReturnsZeroForUnreviewedGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameHistoryQuery history = scope.Games;

        await scope.Games.SaveAsync(TestGameStatsFactory.Create(3001, champion: "Ahri"));

        Assert.Equal(0, await history.GetReviewedCountAsync());
    }

    [Fact]
    public async Task HistoryQuery_GetReviewedCountAsync_CountsPersistedReview()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameHistoryQuery history = scope.Games;

        await scope.Games.SaveAsync(TestGameStatsFactory.Create(3002, champion: "Ahri"));
        await scope.Games.UpdateReviewAsync(3002, new Revu.Core.Models.GameReview
        {
            Notes = "Reviewed the lane phase."
        });

        Assert.Equal(1, await history.GetReviewedCountAsync());
    }
}

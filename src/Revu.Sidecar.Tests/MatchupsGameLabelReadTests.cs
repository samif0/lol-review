using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The matchup journal labels every linked card with its game's date and result. With
/// automatic matchup notes there is one card per game, so the labels come from one read
/// instead of a full game load per card.
/// </summary>
public sealed class MatchupsGameLabelReadTests
{
    private sealed class CountingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public int Opened { get; private set; }
        public string DatabasePath => inner.DatabasePath;
        public SqliteConnection CreateConnection() { Opened++; return inner.CreateConnection(); }
    }

    private static async Task<(int Opened, MatchupsDto Snapshot)> BuildWithLinkedCardsAsync(int cards)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < cards; i++)
        {
            var gameId = 6_100 + i;
            await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, win: i % 2 == 0, timestamp: now - i * 3600));
            await matchups.CreateAsync("mid", ["Ahri"], ["Syndra"], gameId: gameId);
        }
        await scope.Games.SetRemakeVerdictAsync(6_101, true, false, "user");
        // A card whose game row is gone keeps rendering, just without a label.
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(99_999));
        await matchups.CreateAsync("mid", ["Ahri"], ["Zed"], gameId: 99_999);
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys = OFF; DELETE FROM games WHERE game_id = 99999;";
            await cmd.ExecuteNonQueryAsync();
        }

        var counting = new CountingFactory(scope.ConnectionFactory);
        var builder = new MatchupsSnapshotBuilder(new MatchupsRepository(counting),
            new Revu.Core.Data.Repositories.GameRepository(counting, new NoopBackupService()),
            scope.Config, NullLogger<MatchupsSnapshotBuilder>.Instance);
        var snapshot = await builder.BuildAsync();
        return (counting.Opened, snapshot);
    }

    [Fact]
    public async Task LinkedCardLabels_ComeFromOneRead_HoweverManyCardsThereAre()
    {
        var (fewOpened, _) = await BuildWithLinkedCardsAsync(2);
        var (manyOpened, many) = await BuildWithLinkedCardsAsync(25);

        Assert.Equal(fewOpened, manyOpened);
        Assert.Equal(26, many.TotalCount);
    }

    [Fact]
    public async Task LinkedCardLabels_KeepTheDateAndResult_RemakesIncluded()
    {
        var (_, snapshot) = await BuildWithLinkedCardsAsync(3);
        var cards = snapshot.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Cards).ToDictionary(c => c.GameId ?? 0);

        Assert.EndsWith("· Win", cards[6_100].GameLabel);
        Assert.EndsWith("· Remake", cards[6_101].GameLabel);
        Assert.EndsWith("· Win", cards[6_102].GameLabel);
        Assert.Matches(@"^\w{3} \d{1,2}, \d{4} · ", cards[6_100].GameLabel);
        Assert.Equal("", cards[99_999].GameLabel);
    }
}

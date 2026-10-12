using Microsoft.Data.Sqlite;
using Revu.Core.Constants;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// The Patterns page and the dashboard read every card's playlist. One query now serves
/// all cards; each card must still get exactly the moments, in exactly the order, its
/// own single-card read returns.
/// </summary>
public sealed class PatternMomentsBatchReadTests
{
    private sealed class CountingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public int Opened { get; private set; }
        public string DatabasePath => inner.DatabasePath;
        public SqliteConnection CreateConnection() { Opened++; return inner.CreateConnection(); }
    }

    private static EvidenceUpsert Clip(long gameId, long objectiveId, int timeS, string polarity) => new(
        GameId: gameId,
        SourceKind: EvidenceKinds.Clip,
        SourceId: null,
        SourceKey: $"clip:test:{gameId}:{timeS}",
        StartTimeSeconds: timeS,
        EndTimeSeconds: timeS + 20,
        Title: "Saved clip",
        ObjectiveId: objectiveId,
        Polarity: polarity,
        Status: EvidenceStatuses.Evidence);

    private static async Task<IReadOnlyList<ObjectivePatternCard>> SeedAsync(TestDatabaseScope scope)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long oldGame = 7_100, gameA = 7_101, gameB = 7_102;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(oldGame, timestamp: now - 40 * 86_400));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameA, win: false, timestamp: now - 7_200));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameB, timestamp: now - 60));
        await scope.Vod.LinkVodAsync(gameA, @"C:\vods\a.mp4");

        for (var i = 0; i < 3; i++)
        {
            var objectiveId = await scope.Objectives.CreateAsync($"Objective {i}", "macro");
            await scope.Evidence.UpsertAsync(Clip(gameB, objectiveId, 900 + i, EvidencePolarities.Bad));
            await scope.Evidence.UpsertAsync(Clip(gameA, objectiveId, 300 + i, EvidencePolarities.Bad));
            await scope.Evidence.UpsertAsync(Clip(gameA, objectiveId, 100 + i, EvidencePolarities.Neutral));
            // Outside the trend window: in the saved collection, never in the trend.
            await scope.Evidence.UpsertAsync(Clip(oldGame, objectiveId, 50 + i, EvidencePolarities.Bad));
        }

        var cards = await scope.Evidence.GetPatternCardsAsync(limit: int.MaxValue);
        // A legacy key from review history has no playlist.
        return [.. cards, new ObjectivePatternCard(Kind: "isolated_deaths", Title: "legacy", Detail: "")];
    }

    [Fact]
    public async Task EveryCardGetsTheSamePlaylistAsItsOwnRead()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var cards = await SeedAsync(scope);
        Assert.Equal(3, cards.Count(c => c.Kind == PatternConstants.KindSavedObjectiveEvidence));
        Assert.Equal(3, cards.Count(c => c.Kind == PatternConstants.KindBadObjectiveEvidence));

        var batch = await scope.Evidence.GetPatternMomentsAsync(cards);

        Assert.Equal(cards.Select(c => c.PatternKey).Distinct().Count(), batch.Count);
        foreach (var card in cards)
        {
            var single = await scope.Evidence.GetPatternMomentsAsync(card);
            Assert.Equal(single, batch[card.PatternKey]);
            if (card.Kind == PatternConstants.KindSavedObjectiveEvidence) Assert.Equal(4, single.Count);
            else if (card.Kind == PatternConstants.KindBadObjectiveEvidence)
            {
                Assert.Equal(2, single.Count);
                Assert.All(single, m => Assert.Equal(EvidencePolarities.Bad, m.Polarity));
                Assert.Equal(new long[] { 7_101, 7_102 }, single.Select(m => m.GameId));
            }
            else Assert.Empty(single);
        }
    }

    [Fact]
    public async Task AllPlaylistsComeFromOneQuery()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var cards = await SeedAsync(scope);
        var counting = new CountingFactory(scope.ConnectionFactory);

        await new EvidenceRepository(counting).GetPatternMomentsAsync(cards);

        Assert.Equal(1, counting.Opened);
    }
}

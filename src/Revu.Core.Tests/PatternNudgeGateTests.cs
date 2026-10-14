using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The review nudge waits for a habit: at least four unreviewed moments that can still be
/// watched, across at least three games. Unreviewed means saved after the trend's last
/// review. Any review keeps every nudge quiet for 48 hours.
/// </summary>
public sealed class PatternNudgeGateTests
{
    private const string Key = "bad_objective_evidence:obj1";
    private static readonly IReadOnlyDictionary<string, long> NeverReviewed = new Dictionary<string, long>();

    private static PatternMoment Moment(long gameId, long createdAt = 1_000) => new(
        EvidenceId: gameId * 100 + createdAt, GameId: gameId, ChampionName: "Ahri", Win: false,
        GameTimestamp: gameId, StartTimeSeconds: 300, EndTimeSeconds: 330, Title: "Walked into fog",
        Note: "", Polarity: EvidencePolarities.Bad, SourceKind: "bookmark", VodPath: $"C:\\vods\\{gameId}.mp4",
        CreatedAt: createdAt);

    private static PatternReadiness Ready(IReadOnlyList<PatternMoment> moments,
        IReadOnlyDictionary<string, long>? stamps = null, Func<int, bool>? watchable = null) =>
        PatternReviewGate.Readiness(stamps ?? NeverReviewed, Key, moments, watchable ?? (_ => true));

    [Fact]
    public void ThreeMomentsAcrossThreeGames_IsNotReady()
    {
        var readiness = Ready([Moment(1), Moment(2), Moment(3)]);

        Assert.False(readiness.IsReady);
        Assert.Empty(readiness.ReviewIndexes);
        Assert.Equal(3, readiness.UnreviewedWatchableCount);
        Assert.Equal(3, readiness.UnreviewedWatchableGames);
    }

    [Fact]
    public void FourMomentsAcrossTwoGames_IsNotReady()
    {
        var readiness = Ready([Moment(1), Moment(1, 1_001), Moment(2), Moment(2, 1_001)]);

        Assert.False(readiness.IsReady);
        Assert.Equal(4, readiness.UnreviewedWatchableCount);
        Assert.Equal(2, readiness.UnreviewedWatchableGames);
    }

    [Fact]
    public void FourMomentsAcrossThreeGames_IsReady_AndPlaysThemOldestFirst()
    {
        var readiness = Ready([Moment(1), Moment(2), Moment(2, 1_001), Moment(3)]);

        Assert.True(readiness.IsReady);
        Assert.Equal(new[] { 0, 1, 2, 3 }, readiness.ReviewIndexes);
        Assert.Equal(PatternConstants.NudgeMinMoments, readiness.UnreviewedWatchableCount);
        Assert.Equal(PatternConstants.NudgeMinGames, readiness.UnreviewedWatchableGames);
    }

    [Fact]
    public void UnwatchableMoments_DoNotCount_AndAreLeftOutOfTheReview()
    {
        var moments = new[] { Moment(1), Moment(2), Moment(3), Moment(4), Moment(5) };

        // Games 2 and 4 lost their recordings and kept no clip: three watchable remain.
        var tooFew = Ready(moments, watchable: i => i is not (1 or 3));
        Assert.False(tooFew.IsReady);
        Assert.Equal(3, tooFew.UnreviewedWatchableCount);

        // Only game 2 is gone: four watchable across four games, and the review skips it.
        var ready = Ready(moments, watchable: i => i != 1);
        Assert.True(ready.IsReady);
        Assert.Equal(new[] { 0, 2, 3, 4 }, ready.ReviewIndexes);
    }

    [Fact]
    public void AfterAReview_OnlyMomentsSavedSinceThenCount()
    {
        var stamps = new Dictionary<string, long> { [Key] = 2_000 };
        var old = new[] { Moment(1), Moment(2), Moment(3), Moment(4) };

        Assert.False(Ready(old, stamps).IsReady);
        Assert.Equal(0, Ready(old, stamps).UnreviewedWatchableCount);

        // A moment saved at the review instant was part of that review.
        var withNew = old.Concat([Moment(5, 2_000), Moment(5, 2_001), Moment(6, 2_500), Moment(7, 3_000), Moment(1, 3_100)]).ToArray();
        var readiness = Ready(withNew, stamps);
        Assert.True(readiness.IsReady);
        Assert.Equal(new[] { 5, 6, 7, 8 }, readiness.ReviewIndexes);
        Assert.Equal(4, readiness.UnreviewedWatchableGames);
    }

    [Fact]
    public void AnotherTrendsReview_DoesNotHideThisTrendsMoments()
    {
        var stamps = new Dictionary<string, long> { ["bad_objective_evidence:obj2"] = 5_000 };

        Assert.True(Ready([Moment(1), Moment(2), Moment(3), Moment(3, 1_001)], stamps).IsReady);
    }

    [Fact]
    public void AnyReviewInTheLast48Hours_KeepsTheNudgeQuiet()
    {
        const long now = 1_000_000;
        var hour = 3_600L;

        Assert.False(PatternReviewGate.InQuietPeriod(NeverReviewed, now));
        Assert.True(PatternReviewGate.InQuietPeriod(new Dictionary<string, long> { [Key] = now - 47 * hour }, now));
        Assert.True(PatternReviewGate.InQuietPeriod(new Dictionary<string, long>
        {
            [Key] = now - 400 * hour,
            ["bad_objective_evidence:obj2"] = now - 2 * hour,
        }, now));
        Assert.False(PatternReviewGate.InQuietPeriod(new Dictionary<string, long>
        {
            [Key] = now - PatternConstants.NudgeQuietHours * hour,
        }, now));
    }
}

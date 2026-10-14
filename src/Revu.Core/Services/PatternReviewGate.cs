#nullable enable

using Revu.Core.Constants;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

/// <summary>
/// The ONE reviewed/re-arm rule, shared by the Patterns page snapshot and the
/// dashboard nag so the two can never disagree.
///
/// <para>
/// pattern_reviews.reviewed_at is the watermark for the moment-set the user
/// worked through: the moments at review time are exactly the rows with
/// created_at &lt;= reviewed_at. A reviewed pattern stays closed until at least
/// <see cref="PatternConstants.ReArmNewMoments"/> moments were created AFTER the
/// review instant — hysteresis so one fresh moment doesn't re-nag a pattern the
/// user just closed, while a genuinely recurring habit re-arms it. (Before this,
/// reviewed keys were suppressed forever: reviewed_at was written but never
/// read, so each global kind could surface as pending at most once per DB.)
/// </para>
/// </summary>
public static class PatternReviewGate
{
    /// <summary>Moments created after this pattern's review watermark
    /// (0 when the pattern was never reviewed).</summary>
    public static int NewMomentCount(
        IReadOnlyDictionary<string, long> reviewedStamps,
        string patternKey,
        IReadOnlyList<PatternMoment> moments)
    {
        if (!reviewedStamps.TryGetValue(patternKey, out var reviewedAt))
        {
            return 0;
        }
        return moments.Count(m => m.CreatedAt > reviewedAt);
    }

    /// <summary>True while the pattern counts as reviewed: it has a review stamp
    /// and fewer than <see cref="PatternConstants.ReArmNewMoments"/> moments
    /// newer than that stamp.</summary>
    public static bool IsReviewed(
        IReadOnlyDictionary<string, long> reviewedStamps,
        string patternKey,
        IReadOnlyList<PatternMoment> moments)
    {
        if (!reviewedStamps.TryGetValue(patternKey, out var reviewedAt))
        {
            return false;
        }
        return moments.Count(m => m.CreatedAt > reviewedAt) < PatternConstants.ReArmNewMoments;
    }

    /// <summary>
    /// Whether a mistake trend is ready for a back-to-back review, and which moments
    /// that review plays. Unreviewed means created after the trend's review watermark
    /// (every moment when it was never reviewed). Only watchable moments count (the
    /// caller decides: a recording or kept clip on disk), so the review never queues a
    /// moment with nothing to play. Ready needs at least
    /// <see cref="PatternConstants.NudgeMinMoments"/> of them across at least
    /// <see cref="PatternConstants.NudgeMinGames"/> games. Pure: shared by the Patterns
    /// page and the nudge read, so the page and the nudge never disagree.
    /// </summary>
    public static PatternReadiness Readiness(
        IReadOnlyDictionary<string, long> reviewedStamps,
        string patternKey,
        IReadOnlyList<PatternMoment> moments,
        Func<int, bool> isWatchable)
    {
        var stamped = reviewedStamps.TryGetValue(patternKey, out var reviewedAt);
        var queue = new List<int>();
        for (var i = 0; i < moments.Count; i++)
        {
            if (stamped && moments[i].CreatedAt <= reviewedAt) continue;
            if (isWatchable(i)) queue.Add(i);
        }
        var games = queue.Select(i => moments[i].GameId).Distinct().Count();
        var ready = queue.Count >= PatternConstants.NudgeMinMoments && games >= PatternConstants.NudgeMinGames;
        return new PatternReadiness(ready, ready ? queue : Array.Empty<int>(), queue.Count, games);
    }

    /// <summary>True while any pattern was reviewed less than
    /// <see cref="PatternConstants.NudgeQuietHours"/> hours ago: one review is never
    /// followed by another nudge the same evening.</summary>
    public static bool InQuietPeriod(IReadOnlyDictionary<string, long> reviewedStamps, long nowUnixSeconds)
    {
        var quietSeconds = PatternConstants.NudgeQuietHours * 3600L;
        return reviewedStamps.Values.Any(reviewedAt => nowUnixSeconds - reviewedAt < quietSeconds);
    }
}

/// <summary>One trend's review readiness (<see cref="PatternReviewGate.Readiness"/>).</summary>
public sealed record PatternReadiness(
    bool IsReady,
    // 0-based indexes into the trend's oldest-first moments: the unreviewed, watchable
    // ones a back-to-back review plays, ascending. Empty unless ready.
    IReadOnlyList<int> ReviewIndexes,
    int UnreviewedWatchableCount,
    int UnreviewedWatchableGames);

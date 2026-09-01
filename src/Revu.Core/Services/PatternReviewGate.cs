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
}

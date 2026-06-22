using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

// P-037: the mastery gate that unlocks objective completion (skill held over a
// horizon), distinct from the attendance/clipping EFFORT score. Tests the pure
// gate logic: gate=80%, recency=last-3-consecutive OR 8-of-last-10, 5-day floor,
// one-way ratchet. (Bools are oldest→newest; timestamps in seconds.)
public sealed class ObjectiveMasteryTests
{
    private const long Day = 86400L;

    private static IReadOnlyList<bool> Rep(int trueCount, int falseCount, bool trailingTrue = true)
    {
        // Build a list with falseCount misses then trueCount hits (so recent = hits)
        // unless trailingTrue=false, in which case the misses come last.
        var list = new List<bool>();
        if (trailingTrue)
        {
            for (var i = 0; i < falseCount; i++) list.Add(false);
            for (var i = 0; i < trueCount; i++) list.Add(true);
        }
        else
        {
            for (var i = 0; i < trueCount; i++) list.Add(true);
            for (var i = 0; i < falseCount; i++) list.Add(false);
        }
        return list;
    }

    [Fact]
    public void NoGames_NotMet_ZeroPct()
    {
        var m = ObjectivesRepository.ComputeMastery(new List<bool>(), null, null);
        Assert.False(m.Met);
        Assert.Equal(0.0, m.Pct);
        Assert.Equal(0, m.QualifyingGames);
    }

    [Fact]
    public void EightOfEight_AcrossSixDays_Met()
    {
        // 8 hits, span 6 days (>=5), pct 100%, last-3 all hit → MET.
        var s = Rep(8, 0);
        var m = ObjectivesRepository.ComputeMastery(s, 0, 6 * Day);
        Assert.True(m.Met);
        Assert.Equal(1.0, m.Pct);
        Assert.Equal(6, m.SpanDays);
    }

    [Fact]
    public void HighPct_ButSpanUnderFiveDays_NotMet()
    {
        // 8/8 hits but only a 3-day span → fails the horizon floor (the user's
        // "3 days felt too fast" case).
        var s = Rep(8, 0);
        var m = ObjectivesRepository.ComputeMastery(s, 0, 3 * Day);
        Assert.False(m.Met);
    }

    [Fact]
    public void PctBelowEighty_NotMet_EvenWithGoodRecency()
    {
        // 6 hits + 4 misses = 60% overall; recent 3 are hits but pct gate fails.
        var s = Rep(6, 4);
        var m = ObjectivesRepository.ComputeMastery(s, 0, 8 * Day);
        Assert.False(m.Met);
        Assert.Equal(0.6, m.Pct, 3);
    }

    [Fact]
    public void EightOfTenRecent_WithOldMisses_PctAtThreshold_Met()
    {
        // 8 hits then... arrange 10 games where 8 of last 10 hit AND overall >=80%.
        // 2 misses up front, 8 hits → 80% overall, last-3 hit, 8-of-10 hit.
        var s = Rep(8, 2);
        var m = ObjectivesRepository.ComputeMastery(s, 0, 7 * Day);
        Assert.True(m.Met);
        Assert.Equal(0.8, m.Pct, 3);
    }

    [Fact]
    public void RecentMisses_BreakRecency_NotMet()
    {
        // 8 hits but the 2 most-recent are misses (trailingTrue=false): last-3 not
        // all hit, and 8-of-last-10 fails (only 8 hits but they're the old ones —
        // last 10 = all 10, 8 hits = exactly 8 → 8-of-10 passes). Use 7 hits/3 misses
        // trailing to break both recency rules AND pct.
        var s = Rep(7, 3, trailingTrue: false);
        var m = ObjectivesRepository.ComputeMastery(s, 0, 7 * Day);
        Assert.False(m.Met);
    }

    [Fact]
    public void FewerThanMinGames_NotMet()
    {
        // 2 hits only — below the 3-game minimum even at 100%.
        var s = Rep(2, 0);
        var m = ObjectivesRepository.ComputeMastery(s, 0, 9 * Day);
        Assert.False(m.Met);
        Assert.Equal(2, m.QualifyingGames);
    }

    [Fact]
    public void GateMetadata_IsReported()
    {
        var m = ObjectivesRepository.ComputeMastery(Rep(3, 0), 0, 6 * Day);
        Assert.Equal(0.80, m.Threshold, 3);
        Assert.Equal(3, m.MinGames);
        Assert.Equal(5, m.MinHorizonDays);
    }
}

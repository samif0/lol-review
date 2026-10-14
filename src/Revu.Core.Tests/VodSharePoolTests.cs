using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>VodSharePool.FitsAt mirrors the proxy's fitsAt (contract 5.0) exactly.</summary>
public sealed class VodSharePoolTests
{
    private const long Now = 1_760_400_000;

    private static VodSharePool Pool(long used, long vodUsed, params VodSharePoolRelease[] releases) =>
        new(9_000_000_000, 7_000_000_000, used, vodUsed, Math.Max(0, Math.Min(9_000_000_000 - used, 7_000_000_000 - vodUsed)),
            releases);

    [Fact]
    public void FitsNow_ReturnsNow()
    {
        var pool = Pool(1_535_583_333, 1_315_583_333);
        Assert.Equal(Now, pool.FitsAt(1_315_650_000, Now));
        // Exactly the free space still fits.
        Assert.Equal(Now, pool.FitsAt(7_000_000_000 - 1_315_583_333, Now));
    }

    [Fact]
    public void VodSubsetFull_WaitsForTheFirstReleaseThatFreesVodBytes()
    {
        // Total has room, the VOD subset does not; a clip-only release does not help.
        var pool = Pool(6_900_000_000, 6_800_000_000,
            new VodSharePoolRelease(Now + 600, 200_000_000, 0),
            new VodSharePoolRelease(Now + 4200, 1_400_000_000, 1_400_000_000));
        Assert.Equal(Now + 4200, pool.FitsAt(1_000_000_000, Now));
    }

    [Fact]
    public void TotalFull_CountsEveryKindOfRelease()
    {
        // Clips fill the total: the VOD subset is empty but the total has 0.5 GB.
        var pool = Pool(8_500_000_000, 0,
            new VodSharePoolRelease(Now + 600, 300_000_000, 0),
            new VodSharePoolRelease(Now + 4200, 400_000_000, 0));
        Assert.Equal(Now + 600, pool.FitsAt(700_000_000, Now));
        Assert.Equal(Now + 4200, pool.FitsAt(1_000_000_000, Now));
    }

    [Fact]
    public void ReleasesAreCumulative_AndOutOfOrderInputIsSorted()
    {
        var pool = Pool(8_000_000_000, 6_500_000_000,
            new VodSharePoolRelease(Now + 7800, 500_000_000, 500_000_000),
            new VodSharePoolRelease(Now + 600, 500_000_000, 500_000_000));
        Assert.Equal(Now + 600, pool.FitsAt(1_000_000_000, Now));
        Assert.Equal(Now + 7800, pool.FitsAt(1_500_000_000, Now));
    }

    [Fact]
    public void NeverFits_ReturnsNull()
    {
        var pool = Pool(8_900_000_000, 6_900_000_000, new VodSharePoolRelease(Now + 600, 100_000_000, 100_000_000));
        Assert.Null(pool.FitsAt(2_000_000_000, Now));
        Assert.Null(Pool(9_000_000_000, 7_000_000_000).FitsAt(1, Now));
    }

    [Fact]
    public void OverBudgetPool_NeedsReleasesToGetBackAboveZero()
    {
        // A single PUT can push usage past the budget until its recount rolls it back.
        var pool = Pool(9_100_000_000, 0, new VodSharePoolRelease(Now + 600, 1_100_000_000, 0));
        Assert.Equal(0, pool.FreeBytes);
        Assert.Equal(Now + 600, pool.FitsAt(1_000_000_000, Now));
    }
}

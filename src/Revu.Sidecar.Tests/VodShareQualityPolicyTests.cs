using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>The pre-encode quality decision (contract 7.4).</summary>
public sealed class VodShareQualityPolicyTests
{
    private const long Now = 1_760_400_000;

    private static VodSharePool Pool(long free, params VodSharePoolRelease[] releases) =>
        new(9_000_000_000, 7_000_000_000, 7_000_000_000 - free, 7_000_000_000 - free, free, releases);

    [Theory]
    [InlineData(2255.4, 1_315_650_000, 789_390_000)]
    [InlineData(60, 35_000_000, 21_000_000)]
    [InlineData(61, 35_583_334, 21_350_000)]
    [InlineData(0, 0, 0)]
    public void Estimate_IsCeilOfMinutesTimesRate(double duration, long p1080, long p720)
    {
        Assert.Equal(p1080, VodShareQualityPolicy.Estimate(VodShareQuality.P1080, duration));
        Assert.Equal(p720, VodShareQualityPolicy.Estimate(VodShareQuality.P720, duration));
    }

    [Fact]
    public void ANormalGame_Keeps1080p()
    {
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 2255.4, Pool(5_684_416_667), Now);
        Assert.Equal((VodShareQuality.P1080, (string?)null, (string?)null), (d.Quality, d.Reason, d.ErrorCode));
    }

    [Fact]
    public void ARequested720p_Stays720p()
    {
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P720, 2255.4, Pool(5_684_416_667), Now);
        Assert.Equal((VodShareQuality.P720, (string?)null, true), (d.Quality, d.Reason, d.Ok));
    }

    [Fact]
    public void ALongGame_DropsTo720pForSize()
    {
        // 60 minutes at 35 MB/min is 2.1 GB, past 95% of 2 GiB.
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 3600, Pool(6_000_000_000), Now);
        Assert.Equal((VodShareQuality.P720, "size", true), (d.Quality, d.Reason, d.Ok));
        // 58 minutes still fits as 1080p.
        Assert.Equal(VodShareQuality.P1080, VodShareQualityPolicy.Decide(VodShareQuality.P1080, 58 * 60, Pool(6_000_000_000), Now).Quality);
    }

    [Fact]
    public void ATightPool_DropsTo720pForPool()
    {
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 2255.4, Pool(1_000_000_000), Now);
        Assert.Equal((VodShareQuality.P720, "pool", true), (d.Quality, d.Reason, d.Ok));
    }

    [Fact]
    public void AFullPool_IsPoolFull_WithWhenItFits()
    {
        var pool = Pool(500_000_000, new VodSharePoolRelease(Now + 3600, 400_000_000, 400_000_000));
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 2255.4, pool, Now);
        Assert.Equal(("pool_full", (long?)(Now + 3600)), (d.ErrorCode, d.RetryAt));

        var never = VodShareQualityPolicy.Decide(VodShareQuality.P720, 2255.4, Pool(100), Now);
        Assert.Equal(("pool_full", (long?)null), (never.ErrorCode, never.RetryAt));
    }

    [Theory]
    [InlineData(7201)]
    [InlineData(9000)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void PastTwoHoursOrUnknown_IsTooLong(double duration)
    {
        Assert.Equal("too_long", VodShareQualityPolicy.Decide(VodShareQuality.P720, duration, Pool(7_000_000_000), Now).ErrorCode);
    }

    [Fact]
    public void WithoutAPool_OnlyTheSizeRuleApplies()
    {
        Assert.Equal(VodShareQuality.P1080, VodShareQualityPolicy.Decide(VodShareQuality.P1080, 1800, null, Now).Quality);
        Assert.Equal(VodShareQuality.P720, VodShareQualityPolicy.Decide(VodShareQuality.P1080, 4000, null, Now).Quality);
    }

    [Fact]
    public void DiskNeeded_IsTheEstimatePlusTwentyPercentPlus256MiB()
    {
        Assert.Equal(1_578_780_000 + 268_435_456, VodShareQualityPolicy.DiskNeeded(VodShareQuality.P1080, 2255.4));
    }
}

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
    [InlineData(60, 2255.4, 1_502_096_400, 915_692_400)]
    [InlineData(30, 2255.4, 1_355_495_400, 769_091_400)]
    [InlineData(60, 60, 39_960_000, 24_360_000)]
    [InlineData(30, 60, 36_060_000, 20_460_000)]
    [InlineData(60, 0, 0, 0)]
    public void Estimate_IsTheFittedBitratePlusOverheadAndAudio(int fps, double duration, long p1080, long p720)
    {
        Assert.Equal(p1080, VodShareQualityPolicy.Estimate(VodShareQuality.P1080, fps, duration));
        Assert.Equal(p720, VodShareQualityPolicy.Estimate(VodShareQuality.P720, fps, duration));
    }

    [Fact]
    public void ANormalGame_Keeps1080p()
    {
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 2255.4, Pool(5_684_416_667), Now);
        Assert.Equal((VodShareQuality.P1080, (string?)null, (string?)null), (d.Quality, d.Reason, d.ErrorCode));
    }

    [Fact]
    public void ARequested720p_Stays720p()
    {
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P720, 60, 2255.4, Pool(5_684_416_667), Now);
        Assert.Equal((VodShareQuality.P720, (string?)null, true), (d.Quality, d.Reason, d.Ok));
    }

    [Fact]
    public void ALongGame_Keeps1080p_AtAFittedBitrate_UntilItWouldDropUnderTheFloor()
    {
        // An hour fits under 1.9 GiB at 4.2 Mbps: still 1080p.
        var hour = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 3600, Pool(6_000_000_000), Now);
        Assert.Equal((VodShareQuality.P1080, (string?)null, true), (hour.Quality, hour.Reason, hour.Ok));
        // Past about 84 minutes 1080p would fall under 3 Mbps: 720p instead.
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 5100, Pool(6_000_000_000), Now);
        Assert.Equal((VodShareQuality.P720, "size", true), (d.Quality, d.Reason, d.Ok));
    }

    [Fact]
    public void ATightPool_DropsTo720pForPool()
    {
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 2255.4, Pool(1_000_000_000), Now);
        Assert.Equal((VodShareQuality.P720, "pool", true), (d.Quality, d.Reason, d.Ok));
    }

    [Theory]
    [InlineData(60, 2255.4, 1_547_159_292, 943_163_172)]
    [InlineData(30, 2255.4, 1_396_160_262, 792_164_142)]
    [InlineData(60, 0, 0, 0)]
    public void PoolEstimate_IsTheEstimatePlusThreePercent(int fps, double duration, long p1080, long p720)
    {
        Assert.Equal(1.03m, VodShareQualityPolicy.PoolMargin);
        Assert.Equal(p1080, VodShareQualityPolicy.PoolEstimate(VodShareQuality.P1080, fps, duration));
        Assert.Equal(p720, VodShareQualityPolicy.PoolEstimate(VodShareQuality.P720, fps, duration));
    }

    [Fact]
    public void ThePoolEstimate_CoversWhatTheUploadReserves()
    {
        // The upload that starts with the encode reserves the estimate plus 3%; the pool checks
        // plan for the same 3%, so a share the sheet offers is never refused at init for space.
        foreach (var fps in new[] { 30, 60 })
        foreach (var quality in new[] { VodShareQuality.P1080, VodShareQuality.P720 })
        foreach (var d in new[] { 600.0, 2255.4, 3600, 5000, 7200 })
            Assert.True(VodShareQualityPolicy.PoolEstimate(quality, fps, d) >= VodShareEncoderArgs.ReserveBytes(quality, fps, d));
    }

    [Fact]
    public void ANearlyFullPool_PlansForTheMargin_NotTheBareEstimate()
    {
        // A pool with room for the bare 1080p60 estimate but not for it plus 3% moves the share
        // to 720p before encoding.
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 2255.4, Pool(1_520_000_000), Now);
        Assert.Equal((VodShareQuality.P720, "pool", true), (d.Quality, d.Reason, d.Ok));
        Assert.Equal(VodShareQuality.P1080,
            VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 2255.4, Pool(1_547_159_292), Now).Quality);

        // 720p: room for the bare estimate but not the margin is pool_full, with when it fits.
        var pool = Pool(920_000_000, new VodSharePoolRelease(Now + 3600, 23_163_172, 23_163_172));
        var full = VodShareQualityPolicy.Decide(VodShareQuality.P720, 60, 2255.4, pool, Now);
        Assert.Equal(("pool_full", (long?)(Now + 3600)), (full.ErrorCode, full.RetryAt));
        Assert.True(VodShareQualityPolicy.Decide(VodShareQuality.P720, 60, 2255.4, Pool(943_163_172), Now).Ok);
    }

    [Fact]
    public void AFullPool_IsPoolFull_WithWhenItFits()
    {
        var pool = Pool(500_000_000, new VodSharePoolRelease(Now + 3600, 500_000_000, 500_000_000));
        var d = VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 2255.4, pool, Now);
        Assert.Equal(("pool_full", (long?)(Now + 3600)), (d.ErrorCode, d.RetryAt));

        var never = VodShareQualityPolicy.Decide(VodShareQuality.P720, 60, 2255.4, Pool(100), Now);
        Assert.Equal(("pool_full", (long?)null), (never.ErrorCode, never.RetryAt));
    }

    [Theory]
    [InlineData(7201)]
    [InlineData(9000)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void PastTwoHoursOrUnknown_IsTooLong(double duration)
    {
        Assert.Equal("too_long",
            VodShareQualityPolicy.Decide(VodShareQuality.P720, 60, duration, Pool(7_000_000_000), Now).ErrorCode);
    }

    [Fact]
    public void WithoutAPool_OnlyTheSizeRuleApplies()
    {
        Assert.Equal(VodShareQuality.P1080, VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 1800, null, Now).Quality);
        Assert.Equal(VodShareQuality.P720, VodShareQualityPolicy.Decide(VodShareQuality.P1080, 60, 6000, null, Now).Quality);
    }

    [Fact]
    public void DiskNeeded_IsTheEstimatePlusTwentyPercentPlus256MiB()
    {
        Assert.Equal(1_802_515_680 + 268_435_456, VodShareQualityPolicy.DiskNeeded(VodShareQuality.P1080, 60, 2255.4));
    }
}

using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>The exact ffmpeg arguments of the full-game share copy (contract 7.3).</summary>
public sealed class VodShareEncoderArgsTests
{
    private const string Src = @"C:\rec\capture.mp4";
    private const string Out = @"C:\tmp\game-1-x.mp4";

    private static string Line(IReadOnlyList<string> args) => string.Join(" ", args);

    private static string Expected(string vf, string video) =>
        "-hide_banner -nostdin -y -loglevel error -nostats -progress pipe:1 "
        + $"-i {Src} -map 0:v:0 -map 0:a:0? -map_metadata -1 -map_chapters -1 -sn -dn "
        + $"-vf {vf} {video} "
        + "-c:a aac -b:a 128k -ac 2 -ar 48000 -max_muxing_queue_size 1024 "
        + $"-f mp4 {Out}";

    private const string Vf1080p60Nv12 =
        "fps=60,scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic,format=nv12";
    private const string Vf720p30Yuv =
        "fps=30,scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic,format=yuv420p";

    [Theory]
    [InlineData("h264_nvenc", "-c:v h264_nvenc -preset p4 -tune hq -rc vbr -b:v 5000k -maxrate 7500k -bufsize 10000k -spatial_aq 1 -temporal_aq 1 -bf 3 -g 120 -profile:v high")]
    [InlineData("h264_amf", "-c:v h264_amf -usage transcoding -quality balanced -rc vbr_peak -b:v 5000k -maxrate 7500k -bufsize 10000k -g 120 -profile:v high")]
    [InlineData("h264_qsv", "-c:v h264_qsv -preset medium -b:v 5000k -maxrate 7500k -bufsize 10000k -g 120 -profile:v high")]
    [InlineData("h264_mf", "-c:v h264_mf -rate_control pc_vbr -b:v 5000k -maxrate 7500k -bufsize 10000k -g 120")]
    public void Hardware1080p60_ExactArguments(string encoder, string video)
    {
        var args = VodShareEncoderArgs.EncodeArgs(encoder, Src, Out, VodShareQuality.P1080, 60, 2255.4);
        Assert.Equal(Expected(Vf1080p60Nv12, video), Line(args));
    }

    [Fact]
    public void Libx264_720p30_ExactArguments_WithEightThreads()
    {
        var args = VodShareEncoderArgs.EncodeArgs("libx264", Src, Out, VodShareQuality.P720, 30, 2255.4);
        Assert.Equal(Expected(Vf720p30Yuv,
            "-c:v libx264 -preset veryfast -threads 8 -b:v 2500k -maxrate 3750k -bufsize 5000k -g 60 -profile:v high"),
            Line(args));
        Assert.Equal("8", args[args.ToList().IndexOf("-threads") + 1]);
    }

    [Fact]
    public void Libx264_1080p30_UsesYuv420pAndEightThreads()
    {
        var args = VodShareEncoderArgs.EncodeArgs("libx264", Src, Out, VodShareQuality.P1080, 30, 600);
        Assert.Contains("format=yuv420p", args[args.ToList().IndexOf("-vf") + 1]);
        Assert.Contains("-threads 8 -b:v 4500k -maxrate 6750k -bufsize 9000k -g 60", Line(args));
    }

    [Theory]
    [InlineData(VodShareQuality.P1080, 60, 5000, "fps=60,", "-g 120")]
    [InlineData(VodShareQuality.P1080, 30, 4500, "fps=30,", "-g 60")]
    [InlineData(VodShareQuality.P720, 60, 3000, "fps=60,", "-g 120")]
    [InlineData(VodShareQuality.P720, 30, 2500, "fps=30,", "-g 60")]
    public void TheFrameRate_SetsTheFilter_TheBitrate_AndATwoSecondGop(VodShareQuality quality, int fps, int kbps, string vf,
        string gop)
    {
        var line = Line(VodShareEncoderArgs.EncodeArgs("h264_nvenc", Src, Out, quality, fps, 1800));
        Assert.Contains("-vf " + vf, line);
        Assert.Contains($"-b:v {kbps}k ", line);
        Assert.Contains(gop + " ", line);
        Assert.Equal(kbps, VodShareEncoderArgs.NominalKbps(quality, fps));
    }

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_amf")]
    [InlineData("h264_qsv")]
    [InlineData("h264_mf")]
    [InlineData("libx264")]
    public void Every720pEncode_FitsTheBox_AndNeverSeeksOrCopiesTimestamps(string encoder)
    {
        var args = VodShareEncoderArgs.EncodeArgs(encoder, Src, Out, VodShareQuality.P720, 60, 1800);
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        Assert.StartsWith("fps=60,scale=w='min(1280,iw)':h='min(720,ih)'", vf);
        Assert.EndsWith("format=" + (encoder == "libx264" ? "yuv420p" : "nv12"), vf);
        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-t", args);
        Assert.DoesNotContain("-copyts", args);
        // make_zero would shift a B-frame encode by its reorder delay (see EncodeArgs).
        Assert.DoesNotContain("-avoid_negative_ts", args);
        // The moov goes at the end so the upload can run while the encode writes the file.
        Assert.DoesNotContain("-movflags", args);
        Assert.Contains("-g 120", Line(args));
        Assert.Equal(Out, args[^1]);
        Assert.Equal(Src, args[args.ToList().IndexOf("-i") + 1]);
    }

    [Theory]
    [InlineData(2255.4, 2500)]     // 37.6 min
    [InlineData(5982, 2500)]       // about 100 minutes: still the full rate
    [InlineData(6600, 2254)]       // 110 minutes
    [InlineData(7200, 2056)]       // 2 hours
    [InlineData(0, 2500)]
    [InlineData(double.NaN, 2500)]
    public void VideoKbps720p30_FitsLongGamesUnderTheTarget(double duration, int expected)
    {
        Assert.Equal(expected, VodShareEncoderArgs.VideoKbps(VodShareQuality.P720, 30, duration));
    }

    [Theory]
    [InlineData(2255.4, 5000, false)]
    [InlineData(3000, 5000, false)]    // 50 minutes still fits at the full rate
    [InlineData(3600, 4236, false)]    // an hour is fitted down
    [InlineData(5024, 3000, false)]    // the last length that stays 1080p
    [InlineData(5025, 2999, true)]     // past it the game is shared at 720p
    [InlineData(7200, 2056, true)]
    public void VideoKbps1080p60_FitsLongGames_AndFallsBackTo720pUnderTheFloor(double duration, int expected, bool to720)
    {
        Assert.Equal(expected, VodShareEncoderArgs.VideoKbps(VodShareQuality.P1080, 60, duration));
        Assert.Equal(to720, VodShareEncoderArgs.TooLongFor1080(60, duration));
    }

    [Fact]
    public void LongGame720p_RatesFollowTheFittedBitrate()
    {
        var (b, max, buf) = VodShareEncoderArgs.Rates(VodShareQuality.P720, 30, 7200);
        Assert.Equal((2056, 3084, 4112), (b, max, buf));
        var args = Line(VodShareEncoderArgs.VideoArgs("h264_nvenc", VodShareQuality.P720, 30, 6600));
        Assert.Contains("-b:v 2254k -maxrate 3381k -bufsize 4508k", args);
        // The fitted bitrate keeps a 2 h game under 1.9 GiB (video + 128k audio + 4% overhead).
        var bytes = 7200 * (2056 * 1.04 + 128) * 1000 / 8;
        Assert.True(bytes < 2040109465);
    }

    [Theory]
    [InlineData(VodShareQuality.P1080, 60, 2255.4, 1_502_096_400)]
    [InlineData(VodShareQuality.P720, 60, 2255.4, 915_692_400)]
    [InlineData(VodShareQuality.P1080, 30, 2255.4, 1_355_495_400)]
    [InlineData(VodShareQuality.P720, 30, 2255.4, 769_091_400)]
    [InlineData(VodShareQuality.P1080, 60, 60, 39_960_000)]
    [InlineData(VodShareQuality.P1080, 60, 0, 0)]
    public void Estimate_IsTheBitratePlusOverheadAndAudio(VodShareQuality quality, int fps, double duration, long expected)
    {
        Assert.Equal(expected, VodShareEncoderArgs.EstimateBytes(quality, fps, duration));
    }

    [Fact]
    public void Estimates_StayAboveRealCopies_AndAFittedGameUnder2GiB()
    {
        // Real copies: a 37.6-minute game at 720p30 came to 763,897,802 bytes and at 1080p30 to
        // 1,332,220,736; the 1080p60 excerpt measured 3% under its estimate.
        Assert.True(VodShareEncoderArgs.EstimateBytes(VodShareQuality.P720, 30, 2255.4) > 763_897_802);
        Assert.True(VodShareEncoderArgs.EstimateBytes(VodShareQuality.P1080, 30, 2255.4) > 1_332_220_736);
        foreach (var d in new[] { 3600.0, 5024, 7200 })
        {
            var quality = VodShareEncoderArgs.TooLongFor1080(60, d) ? VodShareQuality.P720 : VodShareQuality.P1080;
            Assert.True(VodShareEncoderArgs.ReserveBytes(quality, 60, d) < 2147483648L, $"{d} s");
        }
    }

    [Fact]
    public void Reserve_IsTheEstimatePlusThreePercent()
    {
        Assert.Equal(1_547_159_292, VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 60, 2255.4));
        Assert.Equal(0, VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 60, double.NaN));
    }

    [Theory]
    [InlineData(7200, false)]
    [InlineData(7201, true)]
    [InlineData(20000, true)]
    public void TooLong_IsPastTwoHours(double duration, bool tooLong)
    {
        Assert.Equal(tooLong, VodShareEncoderArgs.IsTooLong(duration));
    }

    [Fact]
    public void ProbeArgs_AreTheSyntheticOneSecondEncode()
    {
        Assert.Equal(
            "-hide_banner -nostdin -loglevel error -f lavfi -i color=c=black:s=1280x720:r=30 -frames:v 30 -vf format=nv12 "
            + "-c:v h264_amf -usage transcoding -quality balanced -rc vbr_peak -b:v 5000k -maxrate 7500k -bufsize 10000k -g 120 -profile:v high "
            + "-an -f null -",
            Line(VodShareEncoderArgs.ProbeArgs("h264_amf")));
        Assert.Contains("-vf format=yuv420p -c:v libx264 -preset veryfast -threads 8", Line(VodShareEncoderArgs.ProbeArgs("libx264")));
    }

    [Theory]
    [InlineData(60, 600)]
    [InlineData(2255.4, 4811)]
    [InlineData(7200, 14700)]
    [InlineData(9000, 14700)]
    [InlineData(double.NaN, 600)]
    public void Timeout_IsTwiceTheDurationPlusFiveMinutes_Clamped(double duration, int expected)
    {
        Assert.Equal(expected, VodShareEncoderArgs.TimeoutSeconds(duration));
    }

    [Theory]
    [InlineData("out_time_us=0", 100.0, 0)]
    [InlineData("out_time_us=34000000", 100.0, 34)]
    [InlineData("out_time_us=34999999", 100.0, 34)]
    [InlineData("out_time_us=100000000", 100.0, 99)]
    [InlineData("out_time_us=250000000", 100.0, 99)]
    [InlineData("out_time_us=-5000", 100.0, 0)]
    [InlineData("progress=end", 100.0, 100)]
    public void Progress_ParsesOutTimeAndEnd(string line, double duration, int expected)
    {
        Assert.Equal(expected, VodShareEncoderArgs.ParseProgressPercent(line, duration));
    }

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time_ms=34000000")]
    [InlineData("progress=continue")]
    [InlineData("frame=120")]
    [InlineData("")]
    [InlineData(null)]
    public void Progress_IgnoresEverythingElse(string? line)
    {
        Assert.Null(VodShareEncoderArgs.ParseProgressPercent(line, 100));
    }

    [Fact]
    public void Listing_MatchesTheSecondToken()
    {
        string[] lines =
        [
            "Encoders:",
            " V....D libx264              libx264 H.264 / AVC",
            " V....D libx264rgb           libx264 H.264 RGB",
            " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)",
            " A....D aac                  AAC (Advanced Audio Coding)",
        ];
        Assert.True(VodShareEncoderArgs.IsListed(lines, "h264_nvenc"));
        Assert.True(VodShareEncoderArgs.IsListed(lines, "libx264"));
        Assert.False(VodShareEncoderArgs.IsListed(lines, "h264_amf"));
        Assert.False(VodShareEncoderArgs.IsListed(lines, "H.264"));
        Assert.False(VodShareEncoderArgs.IsListed(["h264_qsv is mentioned here"], "h264_qsv"));
    }

    [Theory]
    [InlineData(4.956, "4.956")]
    [InlineData(0.0512, "0.0512")]
    public void ASourceThatStartsLate_KeepsItsTimelineWithAnOutputOffset(double start, string text)
    {
        var args = VodShareEncoderArgs.EncodeArgs("libx264", Src, Out, VodShareQuality.P720, 30, 2255.4, start);
        var line = Line(args);
        Assert.Contains($"-max_muxing_queue_size 1024 -output_ts_offset {text} -f mp4 {Out}", line);
        Assert.DoesNotContain("-copyts", args);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.05)]
    [InlineData(-0.021)]
    [InlineData(double.NaN)]
    public void ASourceStartingAtZero_HasNoOffset(double start)
    {
        Assert.Equal(Line(VodShareEncoderArgs.EncodeArgs("h264_nvenc", Src, Out, VodShareQuality.P1080, 60, 600)),
            Line(VodShareEncoderArgs.EncodeArgs("h264_nvenc", Src, Out, VodShareQuality.P1080, 60, 600, start)));
    }

    [Theory]
    [InlineData("  Duration: 00:00:10.02, start: 4.956000, bitrate: 7543 kb/s", 4.956)]
    [InlineData("  Duration: 00:37:35.12, start: 0.000000, bitrate: 6000 kb/s", 0.0)]
    [InlineData("  Duration: 00:00:01.00, start: -0.021333, bitrate: 1 kb/s", -0.021333)]
    public void StartTime_IsParsedFromTheInputSummary(string stderr, double expected)
    {
        Assert.Equal(expected, VodShareEncoderArgs.ParseStartSeconds(stderr)!.Value, 6);
    }

    [Theory]
    [InlineData("  Duration: N/A, bitrate: N/A")]
    [InlineData("moov atom not found")]
    [InlineData("")]
    public void StartTime_IsNullWhenMissing(string stderr) => Assert.Null(VodShareEncoderArgs.ParseStartSeconds(stderr));

    private const string Built60 =
        "  Duration: 00:37:35.38, start: 0.000000, bitrate: 12170 kb/s\n"
        + "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 "
        + "[SAR 1:1 DAR 16:9], 12000 kb/s, 60 fps, 60 tbr, 15360 tbn (default)\n"
        + "  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 161 kb/s (default)";

    [Theory]
    [InlineData(Built60, 60.0)]
    [InlineData("  Stream #0:0: Video: h264 (Main), yuv420p, 1280x720, 4000 kb/s, 29.97 fps, 29.97 tbr, 90k tbn", 29.97)]
    // An audio stream listed first does not hide the video's rate.
    [InlineData("  Stream #0:0: Audio: aac, 48000 Hz\n  Stream #0:1: Video: h264, yuv420p, 1920x1080, 144 fps, 144 tbr, 1k tbn", 144.0)]
    // No fps: the tbr stands in.
    [InlineData("  Stream #0:0: Video: h264 (High), yuv420p, 1920x1080, 25 tbr, 1k tbn", 25.0)]
    public void FrameRate_IsTheFirstVideoStreams(string stderr, double expected)
    {
        Assert.Equal(expected, VodShareEncoderArgs.ParseFrameRate(stderr)!.Value, 3);
    }

    [Theory]
    [InlineData("  Stream #0:0: Audio: aac, 48000 Hz, stereo")]
    [InlineData("  Stream #0:0: Video: mjpeg (Baseline), yuvj420p, 600x600, 90k tbr, 90k tbn (attached pic)")]
    [InlineData("")]
    [InlineData(null)]
    public void FrameRate_IsNullWithoutAVideoRate(string? stderr) => Assert.Null(VodShareEncoderArgs.ParseFrameRate(stderr));

    [Theory]
    [InlineData(60.0, 60)]
    [InlineData(59.94, 60)]
    [InlineData(144.0, 60)]
    [InlineData(50.0, 60)]
    [InlineData(48.0, 30)]
    [InlineData(30.0, 30)]
    [InlineData(29.97, 30)]
    [InlineData(null, 30)]
    [InlineData(double.NaN, 30)]
    public void ShareFrameRate_Is60From50Up_Else30(double? source, int expected)
    {
        Assert.Equal(expected, VodShareEncoderArgs.Fps(source));
    }

    [Fact]
    public void QualityWireStrings_RoundTrip_AndLabelsCarryTheFrameRate()
    {
        Assert.True(VodShareEncoderArgs.TryParseQuality("1080p", out var hd));
        Assert.Equal(VodShareQuality.P1080, hd);
        Assert.True(VodShareEncoderArgs.TryParseQuality(" 720P ", out var sd));
        Assert.Equal(VodShareQuality.P720, sd);
        Assert.False(VodShareEncoderArgs.TryParseQuality("4k", out _));
        Assert.Equal("1080p", VodShareEncoderArgs.Wire(VodShareQuality.P1080));
        Assert.Equal("720p", VodShareEncoderArgs.Wire(VodShareQuality.P720));
        Assert.Equal("1080p60", VodShareEncoderArgs.Label(VodShareQuality.P1080, 60));
        Assert.Equal("720p30", VodShareEncoderArgs.Label(VodShareQuality.P720, 30));
        Assert.Equal(new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264" }, VodShareEncoderArgs.EncoderPriority);
    }
}

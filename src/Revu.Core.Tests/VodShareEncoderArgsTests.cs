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
        + $"-movflags +faststart -f mp4 {Out}";

    private const string Vf1080Nv12 =
        "fps=30,scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic,format=nv12";
    private const string Vf720Yuv =
        "fps=30,scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic,format=yuv420p";

    [Theory]
    [InlineData("h264_nvenc", "-c:v h264_nvenc -preset p5 -tune hq -multipass qres -rc vbr -b:v 4500k -maxrate 6750k -bufsize 9000k -spatial_aq 1 -temporal_aq 1 -bf 3 -g 60 -profile:v high")]
    [InlineData("h264_amf", "-c:v h264_amf -usage transcoding -quality balanced -rc vbr_peak -b:v 4500k -maxrate 6750k -bufsize 9000k -g 60 -profile:v high")]
    [InlineData("h264_qsv", "-c:v h264_qsv -preset medium -b:v 4500k -maxrate 6750k -bufsize 9000k -g 60 -profile:v high")]
    [InlineData("h264_mf", "-c:v h264_mf -rate_control pc_vbr -b:v 4500k -maxrate 6750k -bufsize 9000k -g 60")]
    public void Hardware1080p_ExactArguments(string encoder, string video)
    {
        var args = VodShareEncoderArgs.EncodeArgs(encoder, Src, Out, VodShareQuality.P1080, 2255.4);
        Assert.Equal(Expected(Vf1080Nv12, video), Line(args));
    }

    [Fact]
    public void Libx264_720p_ExactArguments_WithEightThreads()
    {
        var args = VodShareEncoderArgs.EncodeArgs("libx264", Src, Out, VodShareQuality.P720, 2255.4);
        Assert.Equal(Expected(Vf720Yuv,
            "-c:v libx264 -preset medium -threads 8 -b:v 2500k -maxrate 3750k -bufsize 5000k -g 60 -profile:v high"),
            Line(args));
        Assert.Equal("8", args[args.ToList().IndexOf("-threads") + 1]);
    }

    [Fact]
    public void Libx264_1080p_UsesYuv420pAndEightThreads()
    {
        var args = VodShareEncoderArgs.EncodeArgs("libx264", Src, Out, VodShareQuality.P1080, 600);
        Assert.Contains("format=yuv420p", args[args.ToList().IndexOf("-vf") + 1]);
        Assert.Contains("-threads 8 -b:v 4500k -maxrate 6750k -bufsize 9000k", Line(args));
    }

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_amf")]
    [InlineData("h264_qsv")]
    [InlineData("h264_mf")]
    [InlineData("libx264")]
    public void Every720pEncode_FitsTheBox_AndNeverSeeksOrCopiesTimestamps(string encoder)
    {
        var args = VodShareEncoderArgs.EncodeArgs(encoder, Src, Out, VodShareQuality.P720, 1800);
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        Assert.StartsWith("fps=30,scale=w='min(1280,iw)':h='min(720,ih)'", vf);
        Assert.EndsWith("format=" + (encoder == "libx264" ? "yuv420p" : "nv12"), vf);
        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-t", args);
        Assert.DoesNotContain("-copyts", args);
        // make_zero would shift a B-frame encode by its reorder delay (see EncodeArgs).
        Assert.DoesNotContain("-avoid_negative_ts", args);
        Assert.Contains("-g 60", Line(args));
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
    public void VideoKbps720_FitsLongGamesUnderTheTarget(double duration, int expected)
    {
        Assert.Equal(expected, VodShareEncoderArgs.VideoKbps720(duration));
    }

    [Fact]
    public void LongGame720p_RatesFollowTheFittedBitrate()
    {
        var (b, max, buf) = VodShareEncoderArgs.Rates(VodShareQuality.P720, 7200);
        Assert.Equal((2056, 3084, 4112), (b, max, buf));
        var args = Line(VodShareEncoderArgs.VideoArgs("h264_nvenc", VodShareQuality.P720, 6600));
        Assert.Contains("-b:v 2254k -maxrate 3381k -bufsize 4508k", args);
        // The fitted bitrate keeps a 2 h game under 1.9 GiB (video + 128k audio + 4% overhead).
        var bytes = 7200 * (2056 * 1.04 + 128) * 1000 / 8;
        Assert.True(bytes < 2040109465);
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
            + "-c:v h264_amf -usage transcoding -quality balanced -rc vbr_peak -b:v 4500k -maxrate 6750k -bufsize 9000k -g 60 -profile:v high "
            + "-an -f null -",
            Line(VodShareEncoderArgs.ProbeArgs("h264_amf")));
        Assert.Contains("-vf format=yuv420p -c:v libx264 -preset medium -threads 8", Line(VodShareEncoderArgs.ProbeArgs("libx264")));
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
        var args = VodShareEncoderArgs.EncodeArgs("libx264", Src, Out, VodShareQuality.P720, 2255.4, start);
        var line = Line(args);
        Assert.Contains($"-max_muxing_queue_size 1024 -output_ts_offset {text} -movflags +faststart -f mp4 {Out}", line);
        Assert.DoesNotContain("-copyts", args);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.05)]
    [InlineData(-0.021)]
    [InlineData(double.NaN)]
    public void ASourceStartingAtZero_HasNoOffset(double start)
    {
        Assert.Equal(Line(VodShareEncoderArgs.EncodeArgs("h264_nvenc", Src, Out, VodShareQuality.P1080, 600)),
            Line(VodShareEncoderArgs.EncodeArgs("h264_nvenc", Src, Out, VodShareQuality.P1080, 600, start)));
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

    [Fact]
    public void QualityWireStrings_RoundTrip()
    {
        Assert.True(VodShareEncoderArgs.TryParseQuality("1080p", out var hd));
        Assert.Equal(VodShareQuality.P1080, hd);
        Assert.True(VodShareEncoderArgs.TryParseQuality(" 720P ", out var sd));
        Assert.Equal(VodShareQuality.P720, sd);
        Assert.False(VodShareEncoderArgs.TryParseQuality("4k", out _));
        Assert.Equal("1080p", VodShareEncoderArgs.Wire(VodShareQuality.P1080));
        Assert.Equal("720p", VodShareEncoderArgs.Wire(VodShareQuality.P720));
        Assert.Equal(new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264" }, VodShareEncoderArgs.EncoderPriority);
    }
}

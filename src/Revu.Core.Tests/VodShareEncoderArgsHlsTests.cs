using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>The exact ffmpeg arguments of an HLS share run (plan 4.1): fresh, resumed at the edge, and the self-test.</summary>
public sealed class VodShareEncoderArgsHlsTests
{
    private const string Src = @"C:\rec\capture.mp4";

    private static string Line(IReadOnlyList<string> args) => string.Join(" ", args);

    private const string Vf1080p60Nv12 =
        "fps=60,scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic,format=nv12";
    private const string Nvenc1080p60 =
        "-c:v h264_nvenc -preset p4 -tune hq -rc vbr -b:v 5000k -maxrate 7500k -bufsize 10000k -spatial_aq 1 -temporal_aq 1 -bf 3 -g 120 -profile:v high";
    private const string Tail =
        "-c:a aac -b:a 128k -ac 2 -ar 48000 -max_muxing_queue_size 1024 "
        + "-movflags +frag_keyframe+empty_moov+default_base_moof+delay_moov -f mp4 pipe:1";

    [Fact]
    public void FreshRun_IsTodaysEncode_AsOneFragmentedMp4OnStdout()
    {
        var args = VodShareEncoderArgs.HlsEncodeArgs("h264_nvenc", Src, VodShareQuality.P1080, 60, 2255.4);
        Assert.Equal(
            "-hide_banner -nostdin -y -loglevel error -nostats "
            + $"-i {Src} -map 0:v:0 -map 0:a:0? -map_metadata -1 -map_chapters -1 -sn -dn "
            + $"-vf {Vf1080p60Nv12} {Nvenc1080p60} {Tail}",
            Line(args));
        Assert.DoesNotContain("-progress", args);
        Assert.DoesNotContain("-output_ts_offset", args);
        Assert.DoesNotContain("-ss", args);
    }

    [Fact]
    public void ResumedRun_ReadsVideoAtTheEdge_AndAudioATenthEarlier()
    {
        var args = VodShareEncoderArgs.HlsEncodeArgs("h264_nvenc", Src, VodShareQuality.P1080, 60, 2255.4, resumeAtSeconds: 762);
        Assert.Equal(
            "-hide_banner -nostdin -y -loglevel error -nostats "
            + $"-ss 762 -i {Src} -ss 761.9 -i {Src} -map 0:v:0 -map 1:a:0? -map_metadata -1 -map_chapters -1 -sn -dn "
            + $"-vf {Vf1080p60Nv12} {Nvenc1080p60} {Tail}",
            Line(args));
    }

    [Fact]
    public void EveryRunOfOneEncoder_KeepsTheBitrateOfTheWholeGame()
    {
        // The bitrate (and so the init) must not change at a resume: it is fitted to the whole game's length.
        var fresh = VodShareEncoderArgs.HlsEncodeArgs("h264_nvenc", Src, VodShareQuality.P1080, 60, 6000);
        var resumed = VodShareEncoderArgs.HlsEncodeArgs("h264_nvenc", Src, VodShareQuality.P1080, 60, 6000, resumeAtSeconds: 5990);
        string Rates(IReadOnlyList<string> a) => a[a.ToList().IndexOf("-b:v") + 1];
        Assert.Equal(Rates(fresh), Rates(resumed));
        Assert.Equal(VodShareEncoderArgs.VideoKbps(VodShareQuality.P1080, 60, 6000) + "k", Rates(fresh));
    }

    [Fact]
    public void Libx264_GetsNoSceneCutKeyframes_OnlyInHls()
    {
        var hls = Line(VodShareEncoderArgs.HlsEncodeArgs("libx264", Src, VodShareQuality.P720, 30, 1800));
        Assert.Contains("-g 60 -profile:v high -x264-params scenecut=0 -c:a aac", hls);
        Assert.DoesNotContain("scenecut", Line(VodShareEncoderArgs.EncodeArgs("libx264", Src, @"C:\o.mp4", VodShareQuality.P720, 30, 1800)));
        Assert.DoesNotContain("scenecut", Line(VodShareEncoderArgs.HlsEncodeArgs("h264_nvenc", Src, VodShareQuality.P720, 30, 1800)));
    }

    [Theory]
    [InlineData(30, 29.9)]
    [InlineData(2, 1.9)]
    [InlineData(0.05, 0)]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    public void AudioStart_IsATenthBeforeTheVideo_NeverBeforeZero(double resumeAt, double audio)
    {
        Assert.Equal(audio, VodShareEncoderArgs.HlsAudioStartSeconds(resumeAt), 6);
    }

    [Fact]
    public void ResumeFormatting_IsInvariant_AndExact()
    {
        var args = VodShareEncoderArgs.HlsEncodeArgs("h264_nvenc", Src, VodShareQuality.P1080, 60, 3000, resumeAtSeconds: 1234.5);
        Assert.Equal("1234.5", args[args.ToList().IndexOf("-ss") + 1]);
        Assert.Equal("1234.4", args[args.ToList().LastIndexOf("-ss") + 1]);
    }

    [Fact]
    public void SelfTest_EncodesFourSecondsOfTestPatternAndSine_AtTheShareSettings()
    {
        var args = VodShareEncoderArgs.HlsSelfTestArgs("h264_amf");
        var line = Line(args);
        Assert.StartsWith("-hide_banner -nostdin -y -loglevel error -nostats -f lavfi -i testsrc2=size=1920x1080:rate=60 "
            + "-f lavfi -i sine=frequency=440:sample_rate=48000 -t 4 -map 0:v:0 -map 1:a:0 ", line);
        Assert.Contains("-c:v h264_amf", line);
        Assert.Contains("-g 120", line);
        Assert.EndsWith(Tail, line);
    }

    [Fact]
    public void TheConstants_MatchTheProxy()
    {
        Assert.Equal(2, VodShareEncoderArgs.HlsSegmentSeconds);
        Assert.Equal(6, VodShareEncoderArgs.HlsTargetDuration);
        Assert.Equal(0.1, VodShareEncoderArgs.HlsAudioLeadSeconds);
    }
}

using System.Globalization;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>The narrated-clip ffmpeg argument builder (pure) and the probe parser.</summary>
public sealed class NarrationMixerArgsTests
{
    private static MixPlan Plan(int offsetMs = 0, bool duck = true, bool hasAudio = true, double? duration = 42.5) =>
        new(@"C:\clips\a.mkv", @"C:\Revu\Narration\n.webm", @"C:\clips\narrated\a_narrated.mp4",
            offsetMs, 0.8, 1.25, duck, hasAudio, duration);

    private static string Graph(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void DuckOn_SplitsThePaddedVoiceIntoSidechainAndMix()
    {
        var graph = Graph(NarrationMixer.BuildMixArguments(Plan(duck: true)));

        Assert.Equal(
            "[1:a]asetpts=PTS-STARTPTS,highpass=f=80,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=1.25,apad,"
            + "asplit=2[sc][voice];[0:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.8[game];"
            + "[game][sc]sidechaincompress=threshold=0.02:ratio=8:attack=15:release=350[ducked];"
            + "[ducked][voice]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95[aout]",
            graph);
        // apad must come before asplit or the render is cut at the end of the voice.
        Assert.True(graph.IndexOf("apad", StringComparison.Ordinal) < graph.IndexOf("asplit", StringComparison.Ordinal));
    }

    [Fact]
    public void DuckOff_MixesThePaddedVoiceDirectly()
    {
        var graph = Graph(NarrationMixer.BuildMixArguments(Plan(duck: false)));

        Assert.Equal(
            "[1:a]asetpts=PTS-STARTPTS,highpass=f=80,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=1.25,apad[voice];"
            + "[0:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.8[game];"
            + "[game][voice]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95[aout]",
            graph);
        Assert.DoesNotContain("sidechaincompress", graph);
    }

    [Fact]
    public void NoClipAudio_PadsAndTrimsTheVoiceToTheClipLength()
    {
        var graph = Graph(NarrationMixer.BuildMixArguments(Plan(hasAudio: false, duration: 42.5)));

        Assert.Equal(
            "[1:a]asetpts=PTS-STARTPTS,highpass=f=80,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=1.25,"
            + "apad=whole_dur=42.5,atrim=end=42.5[aout]",
            graph);
        Assert.DoesNotContain("[0:a]", graph);
    }

    [Theory]
    [InlineData(-1500, "atrim=start=1.5,asetpts=PTS-STARTPTS,", null)]
    [InlineData(250, null, ",adelay=250:all=1")]
    [InlineData(0, null, null)]
    public void Offset_NegativeTrimsPositiveDelaysZeroDoesNeither(int offsetMs, string? trim, string? delay)
    {
        var graph = Graph(NarrationMixer.BuildMixArguments(Plan(offsetMs: offsetMs)));

        if (trim is null) Assert.DoesNotContain("atrim=start", graph);
        else Assert.Contains("[1:a]asetpts=PTS-STARTPTS," + trim + "highpass=f=80", graph);
        if (delay is null) Assert.DoesNotContain("adelay", graph);
        else Assert.Contains("volume=1.25" + delay + ",apad", graph);
    }

    [Fact]
    public void Arguments_CopyVideo_FastStart_Mp4Output_InSpecOrder()
    {
        var args = NarrationMixer.BuildMixArguments(Plan());

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-y", "-i", @"C:\clips\a.mkv", "-i", @"C:\Revu\Narration\n.webm", "-filter_complex",
                Graph(args), "-map", "0:v:0", "-map", "[aout]", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k",
                "-movflags", "+faststart", @"C:\clips\narrated\a_narrated.mp4",
            },
            args);
        Assert.EndsWith(".mp4", args[^1]);
    }

    [Fact]
    public void Decimals_AreInvariant_UnderAGermanCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            var graph = Graph(NarrationMixer.BuildMixArguments(
                Plan(offsetMs: -1250, hasAudio: false, duration: 12.75) with { NarrationVolume = 1.5 }));

            Assert.Contains("atrim=start=1.25,", graph);
            Assert.Contains("volume=1.5,", graph);
            Assert.Contains("apad=whole_dur=12.75,atrim=end=12.75", graph);
            Assert.DoesNotContain("1,25", graph);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = saved;
        }
    }

    [Fact]
    public void ProbeParser_ReadsAudioAndDuration_AndTreatsNaAsUnknown()
    {
        const string video = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'C:\clips\a.mp4':
              Duration: 00:01:02.48, start: 0.000000, bitrate: 8123 kb/s
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1920x1080, 60 fps
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 160 kb/s (default)
            At least one output file must be specified
            """;
        var probe = NarrationMixer.ParseProbe(video);
        Assert.True(probe.HasAudio);
        Assert.Equal(62.48, probe.DurationSeconds!.Value, 3);

        const string silentWebm = """
            Input #0, matroska,webm, from 'n.webm':
              Duration: N/A, start: 0.000000, bitrate: N/A
              Stream #0:0: Video: vp8, yuv420p, 640x360
            """;
        var noAudio = NarrationMixer.ParseProbe(silentWebm);
        Assert.False(noAudio.HasAudio);
        Assert.Null(noAudio.DurationSeconds);
    }
}

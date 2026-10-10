using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>Transcription chunk planning: silencedetect parsing, cut planning, export.</summary>
public sealed class NarrationTranscriptChunkerTests
{
    // Recorded ffmpeg 8 stderr for a MediaRecorder WebM (no duration header): N/A duration,
    // \r-separated progress lines, a trailing silence that never ends.
    private const string RecordedStderr =
        "Input #0, matroska,webm, from 'C:\\Revu\\Narration\\3f6c.webm':\n"
        + "  Metadata:\n    encoder         : Chrome\n"
        + "  Duration: N/A, start: 0.000000, bitrate: N/A\n"
        + "  Stream #0:0(eng): Audio: opus, 48000 Hz, mono, fltp (default)\n"
        + "Stream mapping:\n  Stream #0:0 -> #0:0 (opus (native) -> pcm_s16le (native))\n"
        + "Output #0, null, to 'pipe:':\n"
        + "[silencedetect @ 000001] silence_start: 12.3405\n"
        + "[silencedetect @ 000001] silence_end: 14.1 | silence_duration: 1.7595\n"
        + "size=N/A time=00:00:31.50 bitrate=N/A speed= 63x    \r"
        + "[silencedetect @ 000001] silence_start: 44.5\n"
        + "[silencedetect @ 000001] silence_end: 47.25 | silence_duration: 2.75\n"
        + "size=N/A time=00:01:02.04 bitrate=N/A speed= 62x    \r"
        + "[silencedetect @ 000001] silence_start: 70.02\n"
        + "[out#0/null @ 000002] video:0KiB audio:6984KiB subtitle:0KiB other streams:0KiB global headers:0KiB muxing overhead: unknown\n"
        + "size=N/A time=00:01:14.52 bitrate=N/A speed= 64x    \n";

    [Fact]
    public void ParseSilenceDetect_ReadsPairs_TrailingStartRunsToTheEnd_AndTheLastProgressTime()
    {
        var (silences, last) = NarrationTranscriptChunker.ParseSilenceDetect(RecordedStderr);

        Assert.Equal(74.52, last!.Value, 3);
        Assert.Equal(3, silences.Count);
        Assert.Equal((12.3405, 14.1), silences[0]);
        Assert.Equal((44.5, 47.25), silences[1]);
        Assert.Equal(70.02, silences[2].Start, 3);
        Assert.Equal(74.52, silences[2].End, 3);
    }

    [Fact]
    public void ParseSilenceDetect_WithoutProgress_HasNoLastTime_AndAnOpenEndedTrailingSilence()
    {
        var (silences, last) = NarrationTranscriptChunker.ParseSilenceDetect("silence_start: 3.5\n");
        Assert.Null(last);
        Assert.Single(silences);
        Assert.True(double.IsPositiveInfinity(silences[0].End));
        Assert.Empty(NarrationTranscriptChunker.ParseSilenceDetect("").Silences);
    }

    [Fact]
    public void PlanCuts_PicksTheSilenceClosestToFiftySeconds_InsideTheWindow()
    {
        var silences = new List<(double, double)>
        {
            (20, 22),     // before the window: ignored
            (41, 42),     // mid 41.5, 8.5 s from target
            (52, 53),     // mid 52.5, 2.5 s from target: wins
            (61, 64),     // mid 62.5, outside [40, 60]
        };
        var cuts = NarrationTranscriptChunker.PlanCuts(100, silences);
        Assert.Equal(52.5, cuts[0], 3);
        Assert.Single(cuts); // 100 - 52.5 <= 60
    }

    [Fact]
    public void PlanCuts_HardCutsAtSixtySeconds_WhenNoSilenceFits()
    {
        var cuts = NarrationTranscriptChunker.PlanCuts(170, new List<(double, double)> { (5, 6), (130, 131) });
        // 0 -> 60 (hard), 60 -> 120 (hard: 130.5 is past 120), stop at 170 - 120 = 50.
        Assert.Equal(new[] { 60.0, 120.0 }, cuts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12.5)]
    [InlineData(60)]
    public void PlanCuts_ShortInput_IsOneChunk(double duration)
    {
        Assert.Empty(NarrationTranscriptChunker.PlanCuts(duration, Array.Empty<(double, double)>()));
    }

    [Fact]
    public void PlanCuts_TenMinutes_GivesAboutTwelveChunks()
    {
        // A speaker who pauses for a breath roughly every 48 s.
        var silences = Enumerable.Range(1, 12).Select(i => (i * 48.0, i * 48.0 + 0.8)).ToList();
        var cuts = NarrationTranscriptChunker.PlanCuts(600, silences);

        Assert.InRange(cuts.Count, 10, 12);
        var bounds = new[] { 0.0 }.Concat(cuts).Concat(new[] { 600.0 }).ToArray();
        for (var i = 1; i < bounds.Length; i++) Assert.InRange(bounds[i] - bounds[i - 1], 1, 60);
        // Every planned cut lands inside a pause.
        Assert.All(cuts, c => Assert.Contains(silences, s => c >= s.Item1 && c <= s.Item2));
    }

    [Fact]
    public void IsSilent_NeedsOneSilenceCoveringTheChunkInterior()
    {
        var silences = new List<(double, double)> { (99.9, 160.0) };
        Assert.True(NarrationTranscriptChunker.IsSilent(100, 150, silences));
        Assert.False(NarrationTranscriptChunker.IsSilent(90, 150, silences));
        Assert.False(NarrationTranscriptChunker.IsSilent(0, 50, Array.Empty<(double, double)>()));
    }

    [Fact]
    public void ParseSegmentList_ReadsFileStartEndRows()
    {
        var rows = NarrationTranscriptChunker.ParseSegmentList("chunk_000.mp3,0.000000,46.020000\r\nchunk_001.mp3,46.020000,93.000000\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(("chunk_001.mp3", 46.02, 93.0), rows[1]);
    }

    [FfmpegFact]
    public async Task ExportChunksAsync_CutsInThePauses_AndFallsBackToProgressTimeWithoutADuration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "chunks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 130 s of tone with a 2 s pause every 47 s (45..47, 92..94), as WebM/Opus.
            var narration = Path.Combine(dir, "voice.webm");
            await FfmpegTestMedia.RunAsync("-hide_banner", "-y", "-f", "lavfi", "-i",
                "aevalsrc=if(lt(mod(t\\,47)\\,45)\\,0.4*sin(2*PI*440*t)\\,0):s=48000:d=130",
                "-c:a", "libopus", "-b:a", "48k", narration);

            var chunker = new NarrationTranscriptChunker(NullLogger<NarrationTranscriptChunker>.Instance);
            var chunks = await chunker.ExportChunksAsync(narration, 0, Path.Combine(dir, "t1"), CancellationToken.None);

            Assert.Equal(3, chunks.Count);
            Assert.Equal(0, chunks[0].StartS, 1);
            Assert.InRange(chunks[1].StartS, 45.5, 46.5);
            Assert.InRange(chunks[2].StartS, 92.5, 93.5);
            Assert.All(chunks, c => Assert.True(File.Exists(c.Path)));
            Assert.All(chunks, c => Assert.False(c.Silent));
            Assert.All(chunks, c => Assert.InRange(new FileInfo(c.Path).Length, 1, NarrationTranscriptChunker.MaxSendBytes));

            var withDuration = await chunker.ExportChunksAsync(narration, 130, Path.Combine(dir, "t2"), CancellationToken.None);
            Assert.Equal(chunks.Select(c => Math.Round(c.StartS)), withDuration.Select(c => Math.Round(c.StartS)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

/// <summary>A [Fact] that is skipped when ffmpeg is not installed.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegTestMedia.FfmpegPath is null) Skip = "ffmpeg is not installed.";
    }
}

internal static class FfmpegTestMedia
{
    private static readonly Lazy<string?> Path = new(() => FfmpegRunner.FindFfmpegAsync().GetAwaiter().GetResult());

    public static string? FfmpegPath => Path.Value;

    public static async Task RunAsync(params string[] args)
    {
        var result = await FfmpegRunner.RunAsync(FfmpegPath!, args, 120, CancellationToken.None);
        Assert.True(result.Succeeded, "ffmpeg failed: " + result.StderrTail[^Math.Min(600, result.StderrTail.Length)..]);
    }
}

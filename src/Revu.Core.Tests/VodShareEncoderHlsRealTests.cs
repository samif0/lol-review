using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Real ffmpeg, opt-in only (REVU_VOD_ENCODE_TEST=1; plan 7.2), never in CI: every working encoder family splits into
/// IDR-first 2.000 s segments that decode and probe at the source's length, a run resumed at 30 s overlaps its audio
/// (never a hole) and keeps the same encoder's init, and NVENC's video is the MP4 path's. Outputs go to
/// REVU_VOD_ENCODE_OUT (else a temp folder); REVU_SHARE_FFMPEG_PATH picks the binary (with REVU_DATA_ROOT set).
/// </summary>
public sealed class VodShareEncoderHlsRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const double SourceSeconds = 60;

    private static string OutDir(string name)
    {
        var root = Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_OUT");
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "vodhls-real");
        var dir = Path.Combine(root, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string> FfmpegAsync()
    {
        var binaries = await VodShareEncoder.DefaultCandidatesAsync(CancellationToken.None);
        Assert.NotEmpty(binaries);
        return binaries[0];
    }

    /// <summary>60 s of testsrc2 at 1080p60 and a sine wave, as a quick mpeg4/aac recording stand-in.</summary>
    private static async Task<string> SourceAsync(string ffmpeg, string dir)
    {
        var source = Path.Combine(dir, "source.mp4");
        var made = await FfmpegRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=60",
             "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:d=60", "-c:v", "mpeg4", "-q:v", "5",
             "-c:a", "aac", "-shortest", source], 600, CancellationToken.None);
        Assert.True(made.Succeeded, made.StderrTail);
        return source;
    }

    private sealed record Run(FfmpegRunResult Result, Fmp4Splitter Splitter, List<HlsSegment> Segments, HlsInit? Init,
        double FirstSegmentMs, double Seconds);

    private static async Task<Run> RunHlsAsync(string ffmpeg, string encoder, string source, string folder, int run = 0,
        int firstSeq = 0, double resumeAt = 0, VodShareQuality quality = VodShareQuality.P1080)
    {
        Directory.CreateDirectory(folder);
        var channel = Channel.CreateUnbounded<HlsItem>();
        var splitter = new Fmp4Splitter(folder, run, firstSeq, resumeAt, VodShareEncoderArgs.HlsAudioStartSeconds(resumeAt),
            channel.Writer);
        var watch = Stopwatch.StartNew();
        double first = -1;
        var reader = Task.Run(async () =>
        {
            var segments = new List<HlsSegment>();
            HlsInit? init = null;
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                if (item is HlsInit i) init = i;
                if (item is HlsSegment s)
                {
                    if (segments.Count == 0) first = watch.Elapsed.TotalMilliseconds;
                    segments.Add(s);
                }
            }
            return (segments, init);
        });
        var args = VodShareEncoderArgs.HlsEncodeArgs(encoder, source, quality, 60, SourceSeconds, resumeAt);
        var result = await FfmpegRunner.RunAsync(ffmpeg, args, 900, CancellationToken.None, onStdout: splitter.RunAsync);
        channel.Writer.TryComplete();
        var (list, initItem) = await reader;
        return new Run(result, splitter, list, initItem, first, watch.Elapsed.TotalSeconds);
    }

    /// <summary>init + segments in one file: fragmented MP4 joins byte for byte.</summary>
    private static string Join(string path, byte[] init, IEnumerable<HlsSegment> segments)
    {
        using var file = File.Create(path);
        file.Write(init);
        foreach (var s in segments) file.Write(File.ReadAllBytes(s.Path));
        return path;
    }

    private static async Task<(string Errors, double? Duration)> DecodeAsync(string ffmpeg, string path)
    {
        var decode = await FfmpegRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-f", "null", "-"],
            900, CancellationToken.None);
        Assert.Equal(0, decode.ExitCode);
        var probe = await FfmpegRunner.RunAsync(ffmpeg, ["-hide_banner", "-i", path], 60, CancellationToken.None);
        return (decode.StderrTail.Trim(), NarrationMixer.ParseProbe(probe.StderrTail).DurationSeconds);
    }

    private static async Task<bool> ProbePassesAsync(string ffmpeg, string encoder)
    {
        var probe = await FfmpegRunner.RunAsync(ffmpeg, VodShareEncoderArgs.ProbeArgs(encoder), 60, CancellationToken.None);
        return probe.Succeeded;
    }

    [VodEncodeFact]
    public Task EveryWorkingEncoder_MakesIdrFirstTwoSecondSegments_ThatDecodeAtTheSourceLength() =>
        EveryFamilyAsync(null, "families");

    /// <summary>
    /// The same on a 60 s cut of a real recording (REVU_VOD_ENCODE_SOURCE, read only): there the copy must also fit the
    /// reservation (1.03 x the size estimate) without a grow. Synthetic test patterns are far harder than a game and only
    /// report it. Measured on League footage (2026-10-15): NVENC 101.1%, AMF 102.3%, MF 97.7%, libx264 99.5%.
    /// </summary>
    [VodEncodeFact(needsSource: true)]
    public async Task EveryWorkingEncoder_OnARealRecording_StaysWithinTheEstimate()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = OutDir("families-real");
        var cut = Path.Combine(dir, "cut60.mp4");
        var copied = await FfmpegRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-ss", "1200", "-i", Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_SOURCE")!,
             "-t", "60", "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-avoid_negative_ts", "make_zero", cut], 300, CancellationToken.None);
        Assert.True(copied.Succeeded, copied.StderrTail);
        await EveryFamilyAsync(cut, "families-real");
    }

    private async Task EveryFamilyAsync(string? recording, string name)
    {
        var ffmpeg = await FfmpegAsync();
        var dir = recording is null ? OutDir(name) : Path.GetDirectoryName(recording)!;
        var source = recording ?? await SourceAsync(ffmpeg, dir);
        var probe = await new NarrationMixer(NullLogger<NarrationMixer>.Instance).ProbeAsync(source, CancellationToken.None);
        var length = probe?.DurationSeconds ?? SourceSeconds;
        var failures = new List<string>();
        var tested = 0;
        foreach (var encoder in VodShareEncoderArgs.EncoderPriority)
        {
            if (!await ProbePassesAsync(ffmpeg, encoder))
            {
                output.WriteLine($"{encoder}: not available here");
                continue;
            }
            tested++;
            var run = await RunHlsAsync(ffmpeg, encoder, source, Path.Combine(dir, encoder));
            if (!run.Result.Succeeded || run.Splitter.Truncated)
            {
                failures.Add($"{encoder}: exit {run.Result.ExitCode} {run.Result.ConsumerError?.Message} {run.Result.StderrTail}");
                continue;
            }
            var full = run.Segments.Take(run.Segments.Count - 1).ToList();
            if (full.Any(s => s.DurationMs != 2000)) failures.Add($"{encoder}: segments of {string.Join(",", run.Segments.Select(s => s.DurationMs).Distinct())} ms");
            var bytes = run.Segments.Sum(s => s.Bytes) + run.Init!.Bytes.Length;
            var estimate = VodShareEncoderArgs.EstimateBytes(VodShareQuality.P1080, 60, length);
            var (errors, duration) = await DecodeAsync(ffmpeg, Join(Path.Combine(dir, encoder + ".mp4"), run.Init.Bytes, run.Segments));
            output.WriteLine($"{encoder}: {run.Segments.Count} segments ({string.Join(",", run.Segments.Select(s => s.DurationMs).Distinct())} ms), "
                + $"first after {run.FirstSegmentMs:F0} ms, {length / run.Seconds:F1}x, {bytes} bytes ({bytes / (double)estimate:P1} of the "
                + $"estimate), init {run.Init.Bytes.Length} B, decodes to {duration:F3} s of {length:F3} s");
            await File.AppendAllTextAsync(Path.Combine(dir, "results.txt"), $"{DateTime.Now:O} {name} {encoder} segments={run.Segments.Count} "
                + $"firstMs={run.FirstSegmentMs:F0} speed={length / run.Seconds:F1}x bytes={bytes} ofEstimate={bytes / (double)estimate:F3} "
                + $"init={run.Init.Bytes.Length} decoded={duration:F3}{Environment.NewLine}");
            if (errors.Length > 0) failures.Add($"{encoder}: decode errors {errors}");
            if (Math.Abs((duration ?? 0) - length) > 0.15) failures.Add($"{encoder}: decodes to {duration} s of {length} s");
            if (recording is not null && bytes > VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 60, length))
                failures.Add($"{encoder}: {bytes} bytes past the reservation for the estimate {estimate}");
        }
        Assert.True(tested > 0, "no encoder worked");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [VodEncodeFact]
    public async Task ARunResumedAt30Seconds_OverlapsItsAudio_KeepsTheInit_AndJoinsWithoutErrors()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = OutDir("resume");
        var source = await SourceAsync(ffmpeg, dir);
        var encoder = VodShareEncoderArgs.EncoderPriority.First(e => ProbePassesAsync(ffmpeg, e).GetAwaiter().GetResult());

        var fresh = await RunHlsAsync(ffmpeg, encoder, source, Path.Combine(dir, "run0"));
        Assert.True(fresh.Result.Succeeded, fresh.Result.StderrTail);
        var resumed = await RunHlsAsync(ffmpeg, encoder, source, Path.Combine(dir, "run1"), run: 1, firstSeq: 15, resumeAt: 30);
        Assert.True(resumed.Result.Succeeded, resumed.Result.StderrTail);
        Assert.Equal((1, 15), (resumed.Init!.Run, resumed.Init.FromSeq));
        Assert.Equal(Enumerable.Range(15, 15), resumed.Segments.Select(s => s.Seq));

        var tracks = Fmp4Splitter.ParseInit(resumed.Init.Bytes);
        var (sequence, tfdt) = Fmp4TestMedia.ReadSegment(File.ReadAllBytes(resumed.Segments[0].Path));
        var video = tracks.Values.Single(t => t.Handler == "vide");
        var audio = tracks.Values.Single(t => t.Handler == "soun");
        output.WriteLine($"{encoder}: resumed video tfdt {tfdt[video.TrackId] / (double)video.Timescale:F4} s, audio "
            + $"{tfdt[audio.TrackId] / (double)audio.Timescale:F4} s, mfhd {sequence}, same init {fresh.Init!.Bytes.SequenceEqual(resumed.Init.Bytes)}");
        Assert.Equal(16u, sequence);
        Assert.Equal(30.0, tfdt[video.TrackId] / (double)video.Timescale, 3);
        // The audio starts 0.1 s early (an overlap; a hole would stall Chromium for good).
        Assert.InRange(tfdt[audio.TrackId] / (double)audio.Timescale, 29.85, 29.95);
        if (encoder == "h264_nvenc") Assert.Equal(fresh.Init!.Bytes, resumed.Init.Bytes);

        var joined = Join(Path.Combine(dir, "joined.mp4"), fresh.Init!.Bytes, fresh.Segments.Take(15).Concat(resumed.Segments));
        var (errors, duration) = await DecodeAsync(ffmpeg, joined);
        output.WriteLine($"joined at 30 s: decodes to {duration:F3} s, errors [{errors}]");
        Assert.Equal("", errors);
        Assert.InRange(duration ?? 0, SourceSeconds - 0.15, SourceSeconds + 0.15);
    }

    [VodEncodeFact]
    public async Task NvencVideo_IsTheMp4PathsVideo()
    {
        var ffmpeg = await FfmpegAsync();
        if (!await ProbePassesAsync(ffmpeg, "h264_nvenc"))
        {
            output.WriteLine("NVENC is not available here");
            return;
        }
        var dir = OutDir("md5");
        var source = await SourceAsync(ffmpeg, dir);
        var mp4 = Path.Combine(dir, "share.mp4");
        var made = await FfmpegRunner.RunAsync(ffmpeg,
            VodShareEncoderArgs.EncodeArgs("h264_nvenc", source, mp4, VodShareQuality.P1080, 60, SourceSeconds), 900,
            CancellationToken.None);
        Assert.True(made.Succeeded, made.StderrTail);
        var hls = await RunHlsAsync(ffmpeg, "h264_nvenc", source, Path.Combine(dir, "hls"));
        var joined = Join(Path.Combine(dir, "hls.mp4"), hls.Init!.Bytes, hls.Segments);

        async Task<string> VideoMd5Async(string path)
        {
            var run = await FfmpegRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-i", path, "-map", "0:v:0",
                "-c", "copy", "-f", "md5", Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".md5")], 300, CancellationToken.None);
            Assert.True(run.Succeeded, run.StderrTail);
            return File.ReadAllText(Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".md5")).Trim();
        }

        var a = await VideoMd5Async(mp4);
        var b = await VideoMd5Async(joined);
        output.WriteLine($"MP4 video {a}, HLS video {b}");
        Assert.Equal(a, b);
    }
}

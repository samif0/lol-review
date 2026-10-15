#nullable enable

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>The ffmpeg binary and H.264 encoder a share copy is made with.</summary>
public sealed record VodEncoderChoice(string FfmpegPath, string Encoder);

/// <summary>A verified share copy on disk.</summary>
public sealed record VodEncodeResult(string OutputPath, long SizeBytes, string Encoder, VodShareQuality Quality, int Fps,
    int VideoKbps);

/// <summary>An encode that failed (<c>encode_failed</c>) or a game too long to share (<c>too_long</c>).</summary>
public sealed class VodEncodeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Makes the full-game share copy (contract 7.3).</summary>
public interface IVodShareEncoder
{
    /// <summary>The first working (binary, encoder) pair, hardware first; null when none works.</summary>
    Task<VodEncoderChoice?> SelectAsync(CancellationToken ct);

    /// <summary>
    /// Encode <paramref name="sourcePath"/> to <paramref name="outputPath"/> at
    /// <paramref name="fps"/> (60 or 30). Percent progress is 0..99 while encoding and 100 at the
    /// end. <paramref name="beforeRetry"/> runs (and is awaited) after a failed run deleted the
    /// output and before a second encoder writes it again from the start. Throws
    /// <see cref="VodEncodeException"/>; the output is deleted on any failure or cancellation.
    /// </summary>
    Task<VodEncodeResult> EncodeAsync(string sourcePath, string outputPath, VodShareQuality quality, int fps,
        double durationSeconds, IProgress<int>? percent, CancellationToken ct,
        Func<CancellationToken, Task>? beforeRetry = null);

    /// <summary>
    /// The first working pair for an HLS share: it passes its probe and its HLS self-test (two IDR-first 2.000 s
    /// fragments), hardware first; null when none does.
    /// </summary>
    Task<VodEncoderChoice?> SelectHlsAsync(CancellationToken ct);

    /// <summary>Select the HLS pair ahead of a share (the share sheet opened, no game in progress).</summary>
    Task PrewarmHlsAsync(CancellationToken ct);

    /// <summary>
    /// Encode <paramref name="request"/> into HLS segments in its folder, announcing each run's init and every
    /// finished segment on <paramref name="writer"/> in order (plan 4.1). A run that fails resumes at the edge: the
    /// same encoder once, then the next qualified one; two runs in a row without a new segment end it there.
    /// <paramref name="mediaSeconds"/> reports where the last finished segment ends. Cancellation kills ffmpeg and
    /// throws; the segments already announced stay on disk.
    /// </summary>
    Task<VodHlsEncodeResult> EncodeHlsAsync(VodHlsEncodeRequest request, ChannelWriter<HlsItem> writer,
        IProgress<double>? mediaSeconds, CancellationToken ct);
}

/// <summary>One ffmpeg run as the encoder sees it. ConsumerError: a binary stdout consumer threw (ffmpeg was killed).</summary>
internal readonly record struct VodEncoderRun(int ExitCode, string StderrTail, bool TimedOut, Exception? ConsumerError = null)
{
    public bool Succeeded => !TimedOut && ExitCode == 0 && ConsumerError is null;
}

/// <summary>Runs ffmpeg (tests inject a fake; production uses <see cref="FfmpegRunner"/>).</summary>
internal delegate Task<VodEncoderRun> VodFfmpegRunner(string ffmpegPath, IReadOnlyList<string> args, int timeoutSeconds,
    Action<string>? onStdoutLine, CancellationToken ct);

/// <summary>Runs ffmpeg with its stdout handed to a binary consumer (tests feed a synthetic stream).</summary>
internal delegate Task<VodEncoderRun> VodFfmpegBinaryRunner(string ffmpegPath, IReadOnlyList<string> args,
    int timeoutSeconds, Func<Stream, CancellationToken, Task> onStdout, CancellationToken ct);

/// <summary>
/// Picks a working H.264 encoder by probing (hardware on any candidate binary beats the CPU
/// encoder), encodes with progress, retries once on another encoder when a run fails, and
/// verifies the result (ftyp, duration). Selection is cached for the process lifetime. A pair
/// is marked broken only when it also fails its probe after a failed run: a bad source, a full
/// disk or a timeout says nothing about the encoder and must not cost later shares the
/// hardware encoder.
/// </summary>
public sealed partial class VodShareEncoder : IVodShareEncoder
{
    private const int ListTimeoutSeconds = 15;
    private const int ProbeTimeoutSeconds = 20;
    private const int DurationProbeTimeoutSeconds = 30;

    private readonly INarrationMixer? _mixer;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _candidates;
    private readonly VodFfmpegRunner _run;
    private readonly VodFfmpegBinaryRunner _runBinary;
    private readonly VodShareMemory? _memory;
    private readonly SemaphoreSlim _selectGate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<string>> _encoderLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Path, string Encoder)> _broken = new();
    private VodEncoderChoice? _cached;

    public VodShareEncoder(INarrationMixer mixer, ILogger<VodShareEncoder> logger, VodShareMemory? memory = null)
        : this(mixer, logger, DefaultCandidatesAsync, RunFfmpegAsync, RunFfmpegBinaryAsync, memory)
    {
    }

    internal VodShareEncoder(INarrationMixer? mixer, ILogger logger,
        Func<CancellationToken, Task<IReadOnlyList<string>>> candidates, VodFfmpegRunner run,
        VodFfmpegBinaryRunner? runBinary = null, VodShareMemory? memory = null)
    {
        _mixer = mixer;
        _logger = logger;
        _candidates = candidates;
        _run = run;
        _runBinary = runBinary ?? RunFfmpegBinaryAsync;
        _memory = memory;
    }

    public async Task<VodEncoderChoice?> SelectAsync(CancellationToken ct)
    {
        await _selectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            _cached = await FindAsync(null, ct).ConfigureAwait(false);
            if (_cached is not null)
                _logger.LogInformation("Share encoder selected: {Encoder} ({Binary})", _cached.Encoder, _cached.FfmpegPath);
            return _cached;
        }
        finally
        {
            _selectGate.Release();
        }
    }

    /// <summary>The next working pair other than <paramref name="skip"/>, for one retry only (not cached).</summary>
    private async Task<VodEncoderChoice?> SelectOtherAsync(VodEncoderChoice skip, CancellationToken ct)
    {
        await _selectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FindAsync(skip, ct).ConfigureAwait(false);
        }
        finally
        {
            _selectGate.Release();
        }
    }

    /// <summary>The first listed pair that passes its probe, hardware first. The caller holds the gate.</summary>
    private async Task<VodEncoderChoice?> FindAsync(VodEncoderChoice? skip, CancellationToken ct)
    {
        var binaries = await _candidates(ct).ConfigureAwait(false);
        foreach (var encoder in VodShareEncoderArgs.EncoderPriority)
        {
            foreach (var binary in binaries)
            {
                ct.ThrowIfCancellationRequested();
                var pair = new VodEncoderChoice(binary, encoder);
                if (_broken.Contains((binary, encoder)) || pair == skip) continue;
                var listed = await ListEncodersAsync(binary, ct).ConfigureAwait(false);
                if (!VodShareEncoderArgs.IsListed(listed, encoder)) continue;
                if (await ProbePassesAsync(pair, ct).ConfigureAwait(false)) return pair;
            }
        }
        _logger.LogWarning("No working H.264 encoder found for full-game sharing ({Count} binaries)", binaries.Count);
        return null;
    }

    /// <summary>The 1 s functional probe of a pair.</summary>
    private async Task<bool> ProbePassesAsync(VodEncoderChoice pair, CancellationToken ct)
    {
        var probe = await _run(pair.FfmpegPath, VodShareEncoderArgs.ProbeArgs(pair.Encoder), ProbeTimeoutSeconds, null, ct)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            _logger.LogInformation("Share encoder {Encoder} on {Binary} did not pass its probe (exit {Exit})",
                pair.Encoder, pair.FfmpegPath, probe.ExitCode);
        }
        return probe.Succeeded;
    }

    public async Task<VodEncodeResult> EncodeAsync(string sourcePath, string outputPath, VodShareQuality quality, int fps,
        double durationSeconds, IProgress<int>? percent, CancellationToken ct,
        Func<CancellationToken, Task>? beforeRetry = null)
    {
        if (quality == VodShareQuality.P720 && VodShareEncoderArgs.IsTooLong(durationSeconds))
            throw new VodEncodeException("too_long", "Games longer than 2 hours can't be shared.");
        fps = VodShareEncoderArgs.Fps(fps);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            var choice = await SelectAsync(ct).ConfigureAwait(false) ?? throw Failed("no encoder");
            var start = await SourceStartAsync(choice.FfmpegPath, sourcePath, ct).ConfigureAwait(false);
            var run = await RunEncodeAsync(choice, sourcePath, outputPath, quality, fps, durationSeconds, start, percent, ct)
                .ConfigureAwait(false);
            if (!run.Succeeded)
            {
                _logger.LogWarning("Share encode with {Encoder} failed (exit {Exit}, timed out {TimedOut}): {Tail}",
                    choice.Encoder, run.ExitCode, run.TimedOut, Tail(run.StderrTail));
                TryDelete(outputPath);
                // Broken only if the pair also fails its probe now. Otherwise the source, the disk
                // or the machine's load failed the run: keep the pair for later shares, and give a
                // timed-out game no second hours-long run.
                var broken = !await ProbePassesAsync(choice, ct).ConfigureAwait(false);
                if (broken) await MarkBrokenAsync(choice, ct).ConfigureAwait(false);
                else if (run.TimedOut) throw Failed("encode timed out with a working encoder");
                var next = broken
                    ? await SelectAsync(ct).ConfigureAwait(false)
                    : await SelectOtherAsync(choice, ct).ConfigureAwait(false);
                choice = next ?? throw Failed("no other encoder");
                if (beforeRetry is not null) await beforeRetry(ct).ConfigureAwait(false);
                run = await RunEncodeAsync(choice, sourcePath, outputPath, quality, fps, durationSeconds, start, percent, ct)
                    .ConfigureAwait(false);
                if (!run.Succeeded)
                {
                    _logger.LogWarning("Share encode retry with {Encoder} failed (exit {Exit}): {Tail}",
                        choice.Encoder, run.ExitCode, Tail(run.StderrTail));
                    if (!await ProbePassesAsync(choice, ct).ConfigureAwait(false))
                        await MarkBrokenAsync(choice, ct).ConfigureAwait(false);
                    throw Failed("encode failed twice");
                }
            }

            var size = await VerifyAsync(choice.FfmpegPath, sourcePath, outputPath, durationSeconds, ct).ConfigureAwait(false);
            percent?.Report(100);
            var kbps = VodShareEncoderArgs.VideoKbps(quality, fps, durationSeconds);
            return new VodEncodeResult(outputPath, size, choice.Encoder, quality, fps, kbps);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    private async Task<VodEncoderRun> RunEncodeAsync(VodEncoderChoice choice, string source, string output,
        VodShareQuality quality, int fps, double duration, double sourceStart, IProgress<int>? percent, CancellationToken ct)
    {
        var last = -1;
        void OnLine(string line)
        {
            var p = VodShareEncoderArgs.ParseProgressPercent(line, duration);
            // progress=end is reported by the caller once the file is verified.
            if (p is null || p >= 100 || p == last) return;
            last = p.Value;
            percent?.Report(p.Value);
        }

        var args = VodShareEncoderArgs.EncodeArgs(choice.Encoder, source, output, quality, fps, duration, sourceStart);
        var watch = Stopwatch.StartNew();
        var run = await _run(choice.FfmpegPath, args, VodShareEncoderArgs.TimeoutSeconds(duration), OnLine, ct)
            .ConfigureAwait(false);
        if (run.Succeeded && duration > 0)
        {
            _logger.LogInformation("Share copy encoded with {Encoder} at {Quality} in {Seconds:F0} s ({Speed:F1}x)",
                choice.Encoder, VodShareEncoderArgs.Label(quality, fps), watch.Elapsed.TotalSeconds,
                duration / Math.Max(0.001, watch.Elapsed.TotalSeconds));
        }
        return run;
    }

    private async Task MarkBrokenAsync(VodEncoderChoice choice, CancellationToken ct)
    {
        await _selectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _broken.Add((choice.FfmpegPath, choice.Encoder));
            if (_cached == choice) _cached = null;
        }
        finally
        {
            _selectGate.Release();
        }
    }

    /// <summary>The source's container start time (0 when unknown), from <c>ffmpeg -i</c> with the encoding binary.</summary>
    private async Task<double> SourceStartAsync(string ffmpeg, string source, CancellationToken ct)
    {
        var run = await _run(ffmpeg, ["-hide_banner", "-i", source], DurationProbeTimeoutSeconds, null, ct).ConfigureAwait(false);
        var start = run.TimedOut ? null : VodShareEncoderArgs.ParseStartSeconds(run.StderrTail);
        if (start > VodShareEncoderArgs.StartOffsetThresholdSeconds)
            _logger.LogInformation("Share source starts at {Start:F3} s; the copy keeps that offset", start);
        return start ?? 0;
    }

    /// <summary>Exists, non-empty, starts with an ftyp box, and lasts as long as the source.</summary>
    private async Task<long> VerifyAsync(string ffmpeg, string source, string output, double duration, CancellationToken ct)
    {
        var info = new FileInfo(output);
        if (!info.Exists || info.Length <= 0) throw Failed("no output");
        if (!HasFtyp(output)) throw Failed("not an mp4");

        var reference = await ProbeDurationAsync(ffmpeg, source, ct).ConfigureAwait(false);
        if (reference is not > 0) reference = duration;
        var actual = await ProbeDurationAsync(ffmpeg, output, ct).ConfigureAwait(false);
        if (actual is null || reference is not > 0) throw Failed("duration unknown");
        var tolerance = Math.Max(2.0, reference.Value * 0.01);
        if (Math.Abs(actual.Value - reference.Value) > tolerance)
            throw Failed($"duration {actual.Value:F1} s vs {reference.Value:F1} s");
        return info.Length;
    }

    /// <summary>The mixer's probe (the sidecar's ffmpeg) first, then the selected binary itself.</summary>
    private async Task<double?> ProbeDurationAsync(string ffmpeg, string path, CancellationToken ct)
    {
        if (_mixer is not null)
        {
            var probe = await _mixer.ProbeAsync(path, ct).ConfigureAwait(false);
            if (probe?.DurationSeconds is { } d && double.IsFinite(d) && d > 0) return d;
        }
        var run = await _run(ffmpeg, ["-hide_banner", "-i", path], DurationProbeTimeoutSeconds, null, ct).ConfigureAwait(false);
        if (run.TimedOut) return null;
        var own = NarrationMixer.ParseProbe(run.StderrTail).DurationSeconds;
        return own is { } value && double.IsFinite(value) && value > 0 ? value : null;
    }

    private async Task<IReadOnlyList<string>> ListEncodersAsync(string binary, CancellationToken ct)
    {
        if (_encoderLists.TryGetValue(binary, out var cached)) return cached;
        var lines = new List<string>();
        var run = await _run(binary, VodShareEncoderArgs.ListEncodersArgs, ListTimeoutSeconds,
            line => { lock (lines) lines.Add(line); }, ct).ConfigureAwait(false);
        IReadOnlyList<string> result;
        lock (lines) result = run.Succeeded ? lines.ToArray() : Array.Empty<string>();
        _encoderLists[binary] = result;
        return result;
    }

    /// <summary>
    /// Pad the copy at <paramref name="path"/> to exactly <paramref name="size"/> bytes with a
    /// trailing MP4 <c>free</c> box (players skip it; checked in Chromium on a moov-at-end copy,
    /// including seeking). False when the copy is already larger, or short by 1 to 7 bytes (too
    /// few for a box header).
    /// </summary>
    public static bool TryPadTo(string path, long size)
    {
        var gap = size - new FileInfo(path).Length;
        if (gap == 0) return true;
        if (gap < 8 || gap > uint.MaxValue) return false;
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        Span<byte> header = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)gap);
        "free"u8.CopyTo(header[4..]);
        fs.Write(header);
        var zeros = new byte[1 << 16];
        for (var left = gap - 8; left > 0; left -= zeros.Length)
            fs.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
        return true;
    }

    internal static bool HasFtyp(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[8];
            if (fs.Read(head) < 8) return false;
            return head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal const string EncodeFailedMessage = "Revu couldn't prepare the video. Try again.";

    private VodEncodeException Failed(string detail)
    {
        _logger.LogWarning("Share copy failed: {Detail}", detail);
        return new VodEncodeException("encode_failed", EncodeFailedMessage);
    }

    private static string Tail(string stderr) => stderr.Length <= 600 ? stderr : stderr[^600..];

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ── Production seams ─────────────────────────────────────────────────────

    /// <summary>
    /// Candidate binaries in order, de-duplicated, existing files only: REVU_SHARE_FFMPEG_PATH
    /// (dev/e2e, only with REVU_DATA_ROOT set), the sidecar's ffmpeg, then the recorder's
    /// bundled revu-media-ffmpeg.exe.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> DefaultCandidatesAsync(CancellationToken ct)
    {
        var sidecarFfmpeg = await FfmpegRunner.FindFfmpegAsync().ConfigureAwait(false);
        return Candidates(Environment.GetEnvironmentVariable("REVU_SHARE_FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("REVU_DATA_ROOT"), sidecarFfmpeg,
            Path.Combine(AppContext.BaseDirectory, "recorder", "revu-media-ffmpeg.exe"), File.Exists);
    }

    /// <summary>Pure candidate ordering (see <see cref="DefaultCandidatesAsync"/>).</summary>
    internal static IReadOnlyList<string> Candidates(string? shareOverride, string? dataRoot, string? sidecarFfmpeg,
        string recorderFfmpeg, Func<string, bool> exists)
    {
        var list = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try
            {
                if (!Path.IsPathFullyQualified(path)) return;
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }
            if (!exists(full)) return;
            if (list.Contains(full, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(full);
        }

        if (!string.IsNullOrWhiteSpace(dataRoot)) Add(shareOverride?.Trim());
        Add(sidecarFfmpeg);
        Add(recorderFfmpeg);
        return list;
    }

    private static async Task<VodEncoderRun> RunFfmpegAsync(string ffmpegPath, IReadOnlyList<string> args,
        int timeoutSeconds, Action<string>? onStdoutLine, CancellationToken ct)
    {
        try
        {
            var run = await FfmpegRunner.RunAsync(ffmpegPath, args, timeoutSeconds, ct, onStdoutLine: onStdoutLine)
                .ConfigureAwait(false);
            return new VodEncoderRun(run.ExitCode, run.StderrTail, run.TimedOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A binary that cannot start (blocked, wrong architecture) is just an unusable candidate.
            return new VodEncoderRun(-1, ex.Message, false);
        }
    }

    private static async Task<VodEncoderRun> RunFfmpegBinaryAsync(string ffmpegPath, IReadOnlyList<string> args,
        int timeoutSeconds, Func<Stream, CancellationToken, Task> onStdout, CancellationToken ct)
    {
        try
        {
            var run = await FfmpegRunner.RunAsync(ffmpegPath, args, timeoutSeconds, ct, onStdout: onStdout)
                .ConfigureAwait(false);
            return new VodEncoderRun(run.ExitCode, run.StderrTail, run.TimedOut, run.ConsumerError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VodEncoderRun(-1, ex.Message, false);
        }
    }
}

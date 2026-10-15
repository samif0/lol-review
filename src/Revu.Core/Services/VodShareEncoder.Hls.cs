#nullable enable

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Revu.Core.Services;

/// <summary>
/// Where an HLS encode starts (plan 4.1): the next run number the proxy has not seen, the next segment's seq, and
/// where on the share's timeline the run picks up (0 for a fresh share; the end of the last finished segment after
/// a pause or a quality switch). DurationSeconds is the whole game's. A run that would need run number
/// <see cref="MaxRuns"/> or more is never started.
/// </summary>
public sealed record VodHlsEncodeRequest(string SourcePath, string Folder, VodShareQuality Quality, int Fps,
    double DurationSeconds, int NextRun = 0, int NextSeq = 0, double ResumeAtSeconds = 0, int MaxRuns = 8);

/// <summary>
/// How an HLS encode ended: Complete when ffmpeg encoded to the end of the source; otherwise it stopped at the edge
/// for <see cref="StopReason"/> (<c>no_progress</c>, <c>no_encoder</c>, <c>max_runs</c>). NextRun, NextSeq and
/// EndSeconds describe the edge either way.
/// </summary>
public sealed record VodHlsEncodeResult(bool Complete, int NextRun, int NextSeq, double EndSeconds, string? Encoder,
    string? StopReason);

/// <summary>HLS full-game sharing (4.4): the self-tested encoder choice and the run loop that resumes at the edge.</summary>
public sealed partial class VodShareEncoder
{
    private const int SelfTestTimeoutSeconds = 60;
    /// <summary>Runs in a row without a new segment before the encode stops at the edge.</summary>
    internal const int NoProgressRunLimit = 2;

    private VodEncoderChoice? _cachedHls;

    /// <summary>The GPU driver versions the self-test cache is keyed on (tests replace it).</summary>
    internal Func<string> DriverVersion { get; set; } = GpuDriverVersion;

    public Task<VodEncoderChoice?> SelectHlsAsync(CancellationToken ct) => SelectHlsCoreAsync(prewarm: false, ct);

    public async Task PrewarmHlsAsync(CancellationToken ct) => await SelectHlsCoreAsync(prewarm: true, ct).ConfigureAwait(false);

    private async Task<VodEncoderChoice?> SelectHlsCoreAsync(bool prewarm, CancellationToken ct)
    {
        await _selectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedHls is not null) return _cachedHls;
            var (choice, passedOverKnownGood) = await FindHlsAsync(null, prewarm, ct).ConfigureAwait(false);
            // A prewarm can run while the GPU is still busy (a game just ended). It never pins a pair that is worse
            // than one that failed only now although it passed its self-test on this machine before.
            if (choice is not null && !(prewarm && passedOverKnownGood)) _cachedHls = choice;
            if (choice is not null)
                _logger.LogInformation("HLS share encoder selected: {Encoder} ({Binary}){Prewarm}", choice.Encoder,
                    choice.FfmpegPath, prewarm ? " ahead of the share" : "");
            return choice;
        }
        finally
        {
            _selectGate.Release();
        }
    }

    private async Task<VodEncoderChoice?> SelectOtherHlsAsync(VodEncoderChoice skip, CancellationToken ct)
    {
        await _selectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await FindHlsAsync(skip, prewarm: false, ct).ConfigureAwait(false)).Choice;
        }
        finally
        {
            _selectGate.Release();
        }
    }

    /// <summary>
    /// The first listed pair (hardware first) that passes its probe and its HLS self-test. A self-test result is
    /// cached per binary, encoder and GPU driver; a prewarm caches only passes, never a failure that may be
    /// passing load. The caller holds the gate. PassedOverKnownGood: a better pair with a cached pass failed now.
    /// </summary>
    private async Task<(VodEncoderChoice? Choice, bool PassedOverKnownGood)> FindHlsAsync(VodEncoderChoice? skip,
        bool prewarm, CancellationToken ct)
    {
        var binaries = await _candidates(ct).ConfigureAwait(false);
        var passedOver = false;
        foreach (var encoder in VodShareEncoderArgs.EncoderPriority)
        {
            foreach (var binary in binaries)
            {
                ct.ThrowIfCancellationRequested();
                var pair = new VodEncoderChoice(binary, encoder);
                if (_broken.Contains((binary, encoder)) || pair == skip) continue;
                var listed = await ListEncodersAsync(binary, ct).ConfigureAwait(false);
                if (!VodShareEncoderArgs.IsListed(listed, encoder)) continue;
                var key = SelfTestKey(pair);
                var cached = _memory?.SelfTest(key);
                if (cached == false) continue;
                if (!await ProbePassesAsync(pair, ct).ConfigureAwait(false))
                {
                    passedOver |= cached == true;
                    continue;
                }
                if (cached == true) return (pair, passedOver);
                var passed = await SelfTestPassesAsync(pair, ct).ConfigureAwait(false);
                if (passed || !prewarm) _memory?.SetSelfTest(key, passed);
                if (passed) return (pair, passedOver);
                passedOver |= prewarm;
            }
        }
        _logger.LogWarning("No H.264 encoder passed the HLS self-test ({Count} binaries)", binaries.Count);
        return (null, passedOver);
    }

    /// <summary>The 4 s self-test: exit 0 and exactly two IDR-first fragments of 2.000 s through the splitter.</summary>
    private async Task<bool> SelfTestPassesAsync(VodEncoderChoice pair, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "revu-hls-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var channel = Channel.CreateUnbounded<HlsItem>();
            var splitter = new Fmp4Splitter(folder, 0, 0, 0, 0, channel.Writer);
            var run = await _runBinary(pair.FfmpegPath, VodShareEncoderArgs.HlsSelfTestArgs(pair.Encoder),
                SelfTestTimeoutSeconds, splitter.RunAsync, ct).ConfigureAwait(false);
            channel.Writer.TryComplete();
            var durations = new List<int>();
            while (channel.Reader.TryRead(out var item))
                if (item is HlsSegment segment) durations.Add(segment.DurationMs);
            var passed = run.Succeeded && !splitter.Truncated && durations.Count == 2 && durations.All(ms => ms == 2000);
            if (!passed)
            {
                _logger.LogInformation("Share encoder {Encoder} on {Binary} did not pass the HLS self-test "
                    + "(exit {Exit}, {Error}, fragments [{Durations}] ms)", pair.Encoder, pair.FfmpegPath, run.ExitCode,
                    run.ConsumerError?.Message ?? "no splitter error", string.Join(", ", durations));
            }
            return passed;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The self-test cache key: the binary (path, size, time), the encoder and the GPU driver.</summary>
    private string SelfTestKey(VodEncoderChoice pair)
    {
        string binary;
        try
        {
            var info = new FileInfo(pair.FfmpegPath);
            binary = $"{info.FullName}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            binary = pair.FfmpegPath;
        }
        return $"{binary}|{pair.Encoder}|{DriverVersion()}";
    }

    /// <summary>Every display adapter's driver description and version, sorted ("unknown" when unreadable).</summary>
    internal static string GpuDriverVersion()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root is null) return "unknown";
            var parts = new List<string>();
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("DriverVersion") is string version && version.Length > 0)
                        parts.Add($"{key.GetValue("DriverDesc") as string}={version}");
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // One adapter's key is locked: the others still describe the machine.
                }
            }
            parts.Sort(StringComparer.Ordinal);
            return parts.Count == 0 ? "unknown" : string.Join(";", parts);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "unknown";
        }
    }

    // ── The run loop ─────────────────────────────────────────────────────────

    public async Task<VodHlsEncodeResult> EncodeHlsAsync(VodHlsEncodeRequest request, ChannelWriter<HlsItem> writer,
        IProgress<double>? mediaSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Quality == VodShareQuality.P720 && VodShareEncoderArgs.IsTooLong(request.DurationSeconds))
            throw new VodEncodeException("too_long", "Games longer than 2 hours can't be shared.");
        Directory.CreateDirectory(request.Folder);
        var fps = VodShareEncoderArgs.Fps(request.Fps);
        var choice = await SelectHlsAsync(ct).ConfigureAwait(false) ?? throw Failed("no encoder passed the HLS self-test");

        var run = request.NextRun;
        var seq = request.NextSeq;
        var at = Math.Max(0, request.ResumeAtSeconds);
        var failuresWithChoice = 0;
        var noProgress = 0;
        while (true)
        {
            if (run >= request.MaxRuns) return Stop("max_runs");
            var result = await RunHlsAsync(choice, request, fps, run, seq, at, writer, mediaSeconds, ct).ConfigureAwait(false);
            if (result.Segments > 0)
            {
                run++;
                seq = result.NextSeq;
                at = result.EndSeconds;
                noProgress = 0;
            }
            else
            {
                noProgress++;
            }

            if (result.Succeeded)
            {
                var shortBy = request.DurationSeconds - at;
                // Never encoded again: a source that is shorter than its probe ends where ffmpeg ended it.
                if (shortBy > Math.Max(2.0, request.DurationSeconds * 0.01))
                {
                    _logger.LogWarning("HLS share encode ended {Short:F1} s short of the probed {Duration:F1} s; ending there",
                        shortBy, request.DurationSeconds);
                }
                return new VodHlsEncodeResult(true, run, seq, at, choice.Encoder, null);
            }
            if (noProgress >= NoProgressRunLimit) return Stop("no_progress");
            if (++failuresWithChoice >= 2)
            {
                // The same encoder failed twice: broken only if its probe fails too; take the next qualified pair.
                if (!await ProbePassesAsync(choice, ct).ConfigureAwait(false)) await MarkBrokenHlsAsync(choice, ct).ConfigureAwait(false);
                var next = await SelectOtherHlsAsync(choice, ct).ConfigureAwait(false);
                if (next is null) return Stop("no_encoder");
                _logger.LogInformation("HLS share encode continues at {At:F1} s with {Encoder}", at, next.Encoder);
                choice = next;
                failuresWithChoice = 0;
            }
            else
            {
                _logger.LogInformation("HLS share encode resumes at {At:F1} s with {Encoder}", at, choice.Encoder);
            }
        }

        VodHlsEncodeResult Stop(string reason)
        {
            _logger.LogWarning("HLS share encode stopped at {At:F1} s of {Duration:F1} s ({Reason})", at,
                request.DurationSeconds, reason);
            return new VodHlsEncodeResult(false, run, seq, at, choice.Encoder, reason);
        }
    }

    /// <summary>One run as the loop sees it.</summary>
    private readonly record struct HlsRun(bool Succeeded, int Segments, int NextSeq, double EndSeconds);

    private async Task<HlsRun> RunHlsAsync(VodEncoderChoice choice, VodHlsEncodeRequest request, int fps, int run, int seq,
        double at, ChannelWriter<HlsItem> writer, IProgress<double>? mediaSeconds, CancellationToken ct)
    {
        var args = VodShareEncoderArgs.HlsEncodeArgs(choice.Encoder, request.SourcePath, request.Quality, fps,
            request.DurationSeconds, at);
        var splitter = new Fmp4Splitter(request.Folder, run, seq, at, VodShareEncoderArgs.HlsAudioStartSeconds(at), writer,
            mediaSeconds);
        var watch = Stopwatch.StartNew();
        var remaining = Math.Max(1, request.DurationSeconds - at);
        var result = await _runBinary(choice.FfmpegPath, args, VodShareEncoderArgs.TimeoutSeconds(remaining), splitter.RunAsync, ct)
            .ConfigureAwait(false);
        var ok = result.Succeeded && !splitter.Truncated;
        if (ok)
        {
            var media = splitter.EndSeconds - at;
            _logger.LogInformation("HLS share run {Run} encoded {Segments} segment(s) with {Encoder} at {Quality} in {Seconds:F0} s ({Speed:F1}x)",
                run, splitter.Segments, choice.Encoder, VodShareEncoderArgs.Label(request.Quality, fps),
                watch.Elapsed.TotalSeconds, media / Math.Max(0.001, watch.Elapsed.TotalSeconds));
        }
        else
        {
            _logger.LogWarning("HLS share run {Run} with {Encoder} failed after {Segments} segment(s) (exit {Exit}, timed out "
                + "{TimedOut}, splitter {Error}, truncated {Truncated}): {Tail}", run, choice.Encoder, splitter.Segments,
                result.ExitCode, result.TimedOut, result.ConsumerError?.Message ?? "ok", splitter.Truncated,
                Tail(result.StderrTail));
        }
        return new HlsRun(ok, splitter.Segments, splitter.NextSeq, splitter.EndSeconds);
    }

    private async Task MarkBrokenHlsAsync(VodEncoderChoice choice, CancellationToken ct)
    {
        await MarkBrokenAsync(choice, ct).ConfigureAwait(false);
        await _selectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedHls == choice) _cachedHls = null;
        }
        finally
        {
            _selectGate.Release();
        }
    }
}

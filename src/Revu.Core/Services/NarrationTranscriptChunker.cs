#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Revu.Core.Constants;

namespace Revu.Core.Services;

/// <summary>One exported transcription chunk; times are seconds on the voice track.</summary>
public sealed record TranscriptChunk(string Path, double StartS, double EndS, bool Silent);

public interface INarrationTranscriptChunker
{
    /// <summary>
    /// Split the voice track into mono 16 kHz MP3 chunks of about 50 s, cut in silences.
    /// <paramref name="durationS"/> comes from the caller (MediaRecorder WebM has no
    /// duration header); 0 or less falls back to the silencedetect pass's last time.
    /// </summary>
    Task<IReadOnlyList<TranscriptChunk>> ExportChunksAsync(string narrationPath, double durationS,
        string tempDir, CancellationToken ct);
}

/// <summary>
/// Plans and exports the transcription chunks of a narration voice track: a silencedetect
/// pass finds pauses, <see cref="PlanCuts"/> picks a cut in a pause near every 50 s (hard cut
/// at 60 s), and one ffmpeg segment pass writes the chunks.
/// </summary>
public sealed partial class NarrationTranscriptChunker : INarrationTranscriptChunker
{
    public const double TargetChunkSeconds = 50;
    public const double MaxChunkSeconds = 60;
    public const double WindowMinSeconds = 40;
    /// <summary>Safety: never send a chunk longer than the proxy's 120 s cap.</summary>
    public const double MaxSendSeconds = 120;
    /// <summary>Safety: 750 KB of MP3 stays under the 1 MiB base64 body cap.</summary>
    public const long MaxSendBytes = 750_000;
    private const double SilentEdgeSeconds = 0.2;

    [GeneratedRegex(@"silence_start:\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex SilenceEndRegex();

    [GeneratedRegex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex TimeRegex();

    private readonly ILogger<NarrationTranscriptChunker> _logger;

    public NarrationTranscriptChunker(ILogger<NarrationTranscriptChunker> logger) => _logger = logger;

    /// <summary>
    /// Read <c>silence_start</c>/<c>silence_end</c> pairs from silencedetect stderr. A trailing
    /// start with no end runs to the end (the last progress time, else +infinity).
    /// <c>LastTimeSeconds</c> is the last <c>time=HH:MM:SS.xx</c> progress value.
    /// </summary>
    public static (IReadOnlyList<(double Start, double End)> Silences, double? LastTimeSeconds) ParseSilenceDetect(string stderr)
    {
        stderr ??= "";
        double? lastTime = null;
        foreach (Match m in TimeRegex().Matches(stderr))
        {
            lastTime = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        // Walk start/end markers in stream order.
        var markers = new List<(int Index, bool IsStart, double Value)>();
        foreach (Match m in SilenceStartRegex().Matches(stderr))
            markers.Add((m.Index, true, double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
        foreach (Match m in SilenceEndRegex().Matches(stderr))
            markers.Add((m.Index, false, double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
        markers.Sort((a, b) => a.Index.CompareTo(b.Index));

        var silences = new List<(double Start, double End)>();
        double? open = null;
        foreach (var (_, isStart, value) in markers)
        {
            if (isStart)
            {
                open = Math.Max(0, value);
            }
            else if (open is { } start)
            {
                if (value > start) silences.Add((start, value));
                open = null;
            }
        }
        if (open is { } trailing)
        {
            var end = lastTime is { } t && t > trailing ? t : double.PositiveInfinity;
            silences.Add((trailing, end));
        }
        return (silences, lastTime);
    }

    /// <summary>
    /// Cut points: from <c>last</c>, aim at <c>last + 50</c>; take the midpoint of a silence
    /// lying in <c>[last + 40, last + 60]</c> closest to the target, else hard-cut at
    /// <c>last + 60</c>. Stops once at most 60 s remain.
    /// </summary>
    public static IReadOnlyList<double> PlanCuts(double durationS, IReadOnlyList<(double Start, double End)> silences)
    {
        var cuts = new List<double>();
        if (!double.IsFinite(durationS) || durationS <= 0) return cuts;
        var mids = (silences ?? Array.Empty<(double, double)>())
            .Where(s => double.IsFinite(s.Start) && double.IsFinite(s.End) && s.End > s.Start)
            .Select(s => (s.Start + s.End) / 2)
            .OrderBy(m => m)
            .ToArray();

        var last = 0.0;
        while (durationS - last > MaxChunkSeconds)
        {
            var target = last + TargetChunkSeconds;
            var lo = last + WindowMinSeconds;
            var hi = last + MaxChunkSeconds;
            double? best = null;
            foreach (var mid in mids)
            {
                if (mid < lo || mid > hi) continue;
                if (best is null || Math.Abs(mid - target) < Math.Abs(best.Value - target)) best = mid;
            }
            var cut = Math.Round(best ?? hi, 3, MidpointRounding.AwayFromZero);
            cuts.Add(cut);
            last = cut;
        }
        return cuts;
    }

    /// <summary>
    /// True when one silence covers the chunk's interior <c>[start + 0.2, end - 0.2]</c>.
    /// </summary>
    public static bool IsSilent(double startS, double endS, IReadOnlyList<(double Start, double End)> silences)
    {
        var innerStart = startS + SilentEdgeSeconds;
        var innerEnd = endS - SilentEdgeSeconds;
        if (innerEnd <= innerStart) innerEnd = innerStart;
        return silences.Any(s => s.Start <= innerStart && s.End >= innerEnd);
    }

    /// <summary>Parse the ffmpeg segment list CSV (<c>file,start,end</c>).</summary>
    public static IReadOnlyList<(string File, double Start, double End)> ParseSegmentList(string csv)
    {
        var rows = new List<(string, double, double)>();
        foreach (var rawLine in (csv ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)) continue;
            if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)) continue;
            var file = string.Join(",", parts[..^2]).Trim().Trim('"');
            rows.Add((file, start, end));
        }
        return rows;
    }

    public async Task<IReadOnlyList<TranscriptChunk>> ExportChunksAsync(string narrationPath, double durationS,
        string tempDir, CancellationToken ct)
    {
        var ffmpeg = await FfmpegRunner.FindFfmpegAsync(_logger).ConfigureAwait(false)
            ?? throw new TranscriptionException(TranscriptionErrorKind.Failed, "ffmpeg not found.");
        if (!File.Exists(narrationPath))
            throw new TranscriptionException(TranscriptionErrorKind.Failed, "Narration audio is missing.");
        Directory.CreateDirectory(tempDir);

        var budget = durationS > 0 ? durationS : GameConstants.MaxClipSeconds;
        var detect = await FfmpegRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-i", narrationPath, "-af", "silencedetect=noise=-40dB:d=0.35", "-f", "null", "-"],
            GameConstants.TranscribeExportTimeoutSeconds(budget), ct, maxStderrChars: 8 * 1024 * 1024).ConfigureAwait(false);
        if (!detect.Succeeded)
            throw new TranscriptionException(TranscriptionErrorKind.Failed,
                detect.TimedOut ? "Silence detection timed out." : $"Silence detection failed ({detect.ExitCode}).");

        var (silences, lastTime) = ParseSilenceDetect(detect.StderrTail);
        var duration = durationS > 0 ? durationS : lastTime ?? 0;
        if (duration <= 0)
            throw new TranscriptionException(TranscriptionErrorKind.Failed, "Narration duration is unknown.");
        var clamped = silences.Select(s => (s.Start, Math.Min(s.End, duration))).ToList();
        var cuts = PlanCuts(duration, clamped);

        var csvPath = Path.Combine(tempDir, "segs.csv");
        var args = new List<string>
        {
            "-hide_banner", "-y", "-i", narrationPath,
            "-vn", "-ac", "1", "-ar", "16000", "-c:a", "libmp3lame", "-b:a", "24k",
            "-f", "segment",
        };
        if (cuts.Count > 0)
        {
            args.Add("-segment_times");
            args.Add(string.Join(",", cuts.Select(c => c.ToString("0.###", CultureInfo.InvariantCulture))));
        }
        args.AddRange(["-segment_list", csvPath, "-segment_list_type", "csv", "-reset_timestamps", "1",
            Path.Combine(tempDir, "chunk_%03d.mp3")]);

        var export = await FfmpegRunner.RunAsync(ffmpeg, args, GameConstants.TranscribeExportTimeoutSeconds(duration), ct)
            .ConfigureAwait(false);
        if (!export.Succeeded || !File.Exists(csvPath))
            throw new TranscriptionException(TranscriptionErrorKind.Failed,
                export.TimedOut ? "Audio export timed out." : $"Audio export failed ({export.ExitCode}).");

        var chunks = new List<TranscriptChunk>();
        foreach (var (file, start, end) in ParseSegmentList(await File.ReadAllTextAsync(csvPath, ct).ConfigureAwait(false)))
        {
            var path = Path.Combine(tempDir, Path.GetFileName(file));
            if (!File.Exists(path)) continue;
            var length = end - start;
            var bytes = new FileInfo(path).Length;
            if (length > MaxSendSeconds || bytes > MaxSendBytes)
            {
                _logger.LogWarning("Transcription chunk {File} is {Seconds:F1}s / {Bytes} bytes; refusing to send", file, length, bytes);
                throw new TranscriptionException(TranscriptionErrorKind.Failed, "A transcription chunk is too large.");
            }
            chunks.Add(new TranscriptChunk(path, start, end, IsSilent(start, end, clamped)));
        }
        if (chunks.Count == 0)
            throw new TranscriptionException(TranscriptionErrorKind.Failed, "Audio export produced no chunks.");
        return chunks;
    }
}

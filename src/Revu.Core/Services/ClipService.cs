#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using Revu.Core.Constants;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Clip extraction from VODs using ffmpeg and folder size management.
/// Ported from Python clips.py.
/// </summary>
public sealed partial class ClipService : IClipService
{
    private static readonly HashSet<string> ClipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".webm"
    };

    /// <summary>Narrated renders live in this subfolder of the clip's folder (C8).</summary>
    public const string NarratedFolderName = "narrated";

    [GeneratedRegex(@"[^A-Za-z0-9_\-]")]
    private static partial Regex UnsafeCharsRegex();

    private readonly IConfigService _config;
    private readonly ILogger<ClipService> _logger;
    private readonly IClipRetentionGuard? _retention;

    public ClipService(IConfigService config, ILogger<ClipService> logger, IClipRetentionGuard? retention = null)
    {
        _config = config;
        _logger = logger;
        _retention = retention;
    }

    /// <inheritdoc />
    public Task<string?> FindFfmpegAsync() => FfmpegRunner.FindFfmpegAsync(_logger);

    /// <inheritdoc />
    public async Task<string?> ExtractClipAsync(
        string vodPath,
        int startS,
        int endS,
        string champion,
        string? outputFolder = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ffmpeg = await FindFfmpegAsync().ConfigureAwait(false);
        if (ffmpeg is null)
        {
            _logger.LogError("ffmpeg not found -- install ffmpeg or add it to PATH");
            return null;
        }

        if (endS <= startS)
        {
            _logger.LogError("Invalid clip range: {Start}s to {End}s", startS, endS);
            return null;
        }

        if (!File.Exists(vodPath))
        {
            _logger.LogError("VOD file not found: {Path}", vodPath);
            return null;
        }

        (double Start, double End) media;
        try { media = RecordingTimeline.ToMediaRange(vodPath, startS, endS); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not align game clip range with the recording");
            return null;
        }

        var clipsDir = outputFolder ?? _config.ClipsFolder;
        Directory.CreateDirectory(clipsDir);

        // Build a descriptive filename
        var duration = endS - startS;
        var startMmSs = $"{startS / 60}-{startS % 60:D2}";
        var safeChamp = string.IsNullOrWhiteSpace(champion)
            ? "clip"
            : UnsafeCharsRegex().Replace(champion.Replace(' ', '_'), "");
        if (string.IsNullOrEmpty(safeChamp)) safeChamp = "clip";
        var timestampStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var sourceExt = Path.GetExtension(vodPath).ToLowerInvariant();
        if (string.IsNullOrEmpty(sourceExt)) sourceExt = ".mp4";
        var filename = $"{safeChamp}_{startMmSs}_{duration}s_{timestampStr}{sourceExt}";
        var outputPath = Path.Combine(clipsDir, filename);

        // Attempt 1: Stream copy (fast, no CPU load, keyframe-aligned)
        var result = await RunFfmpegClipAsync(
            ffmpeg, vodPath, media.Start, media.End, outputPath,
            ["-c", "copy", "-avoid_negative_ts", "make_zero"],
            GameConstants.CopyTimeoutSeconds(duration), ct).ConfigureAwait(false);

        if (result is null)
        {
            if (duration > GameConstants.ReencodeFallbackMaxClipSeconds)
            {
                // A re-encode of a long clip cannot finish inside the request budget.
                _logger.LogWarning("Stream copy failed for a {Duration}s clip; too long for the re-encode fallback", duration);
                throw new ClipExtractionException(ClipExtractionFailure.LongClipCopyFailed,
                    "Revu could not save this clip. Try a shorter range.");
            }

            _logger.LogWarning("Stream copy failed, falling back to re-encode");

            // Attempt 2: Lightweight re-encode
            result = await RunFfmpegClipAsync(
                ffmpeg, vodPath, media.Start, media.End, outputPath,
                [
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", GameConstants.FfmpegCrf.ToString(CultureInfo.InvariantCulture),
                    "-c:a", "aac", "-b:a", "128k",
                    "-threads", "2",
                    "-movflags", "+faststart",
                ],
                GameConstants.FfmpegReEncodeTimeoutS, ct).ConfigureAwait(false);
        }

        if (result is not null)
        {
            try
            {
                await EnforceFolderSizeLimitAsync(clipsDir,
                    (long)_config.ClipsMaxSizeMb * 1024 * 1024, justWritten: result, ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never fail a saved clip because housekeeping failed.
                _logger.LogWarning(ex, "Clips folder size enforcement failed");
            }
        }

        return result;
    }

    // ── Private helpers ─────────────────────────────────────────────

    private async Task<string?> RunFfmpegClipAsync(
        string ffmpeg,
        string vodPath,
        double startS,
        double endS,
        string outputPath,
        string[] extraArgs,
        int timeoutS,
        CancellationToken ct)
    {
        var duration = endS - startS;
        var args = new List<string>
        {
            "-y",
            "-ss", startS.ToString("0.######", CultureInfo.InvariantCulture),
            "-i", vodPath,
            "-t", duration.ToString("0.######", CultureInfo.InvariantCulture),
        };
        args.AddRange(extraArgs);
        args.Add(outputPath);

        _logger.LogInformation("ffmpeg cmd: {Exe} {Args}", ffmpeg, string.Join(" ", args));

        FfmpegRunResult run;
        try
        {
            run = await FfmpegRunner.RunAsync(ffmpeg, args, timeoutS, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(outputPath);
            _logger.LogInformation("Clip extraction cancelled; partial output removed");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clip extraction failed");
            TryDeleteFile(outputPath);
            return null;
        }

        if (run.TimedOut)
        {
            _logger.LogError("ffmpeg timed out after {Timeout}s", timeoutS);
            TryDeleteFile(outputPath);
            return null;
        }

        if (run.ExitCode != 0)
        {
            var stderrTail = run.StderrTail.Length > 500 ? run.StderrTail[^500..] : run.StderrTail;
            _logger.LogError("ffmpeg failed (rc={Code}): {Stderr}", run.ExitCode, stderrTail);
            TryDeleteFile(outputPath);
            return null;
        }

        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            _logger.LogInformation("Clip saved: {Path}", outputPath);
            return outputPath;
        }

        _logger.LogError("ffmpeg returned 0 but no output file was created");
        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup */ }
    }
}

#nullable enable

using System.Diagnostics;
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

    [GeneratedRegex(@"[^A-Za-z0-9_\-]")]
    private static partial Regex UnsafeCharsRegex();

    private readonly IConfigService _config;
    private readonly ILogger<ClipService> _logger;

    public ClipService(IConfigService config, ILogger<ClipService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string?> FindFfmpegAsync()
    {
        return Task.Run<string?>(() =>
        {
            // 1. Check next to the running exe (bundled)
            var exeDir = AppContext.BaseDirectory;
            var bundled = Path.Combine(exeDir, "ffmpeg.exe");
            _logger.LogDebug("Checking bundled ffmpeg: {Path}", bundled);
            if (File.Exists(bundled)) { _logger.LogInformation("Found ffmpeg (bundled): {Path}", bundled); return bundled; }

            // 2. Check PATH
            _logger.LogDebug("Searching PATH for ffmpeg");
            var pathResult = FindInPath("ffmpeg");
            if (pathResult is not null) { _logger.LogInformation("Found ffmpeg in PATH: {Path}", pathResult); return pathResult; }

            // 3. Check common Windows locations
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // WinGet packages use a glob-like path; enumerate manually
            var wingetBase = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            string? wingetFfmpeg = null;
            if (Directory.Exists(wingetBase))
            {
                wingetFfmpeg = Directory.EnumerateFiles(wingetBase, "ffmpeg.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }

            string?[] commonPaths =
            [
                Path.Combine(localAppData, "Revu", "ffmpeg.exe"),
                Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"),
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\ffmpeg\ffmpeg.exe",
                Path.Combine(userProfile, "scoop", "shims", "ffmpeg.exe"),
                wingetFfmpeg,
            ];

            foreach (var path in commonPaths)
            {
                if (path is null) continue;
                _logger.LogDebug("Checking ffmpeg path: {Path}", path);
                if (File.Exists(path)) { _logger.LogInformation("Found ffmpeg: {Path}", path); return path; }
            }

            _logger.LogWarning("ffmpeg not found in any checked location");
            return null;
        });
    }

    /// <inheritdoc />
    public async Task<string?> ExtractClipAsync(
        string vodPath,
        int startS,
        int endS,
        string champion,
        string? outputFolder = null)
    {
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
            ffmpeg, vodPath, startS, endS, outputPath,
            ["-c", "copy", "-avoid_negative_ts", "make_zero"],
            GameConstants.FfmpegClipTimeoutS).ConfigureAwait(false);

        if (result is not null)
        {
            await EnforceFolderSizeLimitAsync(clipsDir,
                (long)_config.ClipsMaxSizeMb * 1024 * 1024).ConfigureAwait(false);
            return result;
        }

        _logger.LogWarning("Stream copy failed, falling back to re-encode");

        // Attempt 2: Lightweight re-encode
        result = await RunFfmpegClipAsync(
            ffmpeg, vodPath, startS, endS, outputPath,
            [
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", GameConstants.FfmpegCrf.ToString(),
                "-c:a", "aac", "-b:a", "128k",
                "-threads", "2",
                "-movflags", "+faststart",
            ],
            GameConstants.FfmpegReEncodeTimeoutS).ConfigureAwait(false);

        if (result is not null)
        {
            await EnforceFolderSizeLimitAsync(clipsDir,
                (long)_config.ClipsMaxSizeMb * 1024 * 1024).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public Task EnforceFolderSizeLimitAsync(string folder, long maxSizeBytes)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(folder)) return;

            var files = new DirectoryInfo(folder)
                .EnumerateFiles()
                .Where(f => ClipExtensions.Contains(f.Extension))
                .Select(f => (File: f, f.LastWriteTimeUtc, f.Length))
                .OrderBy(x => x.LastWriteTimeUtc) // oldest first
                .ToList();

            var totalBytes = files.Sum(x => x.Length);
            int deleted = 0;

            while (totalBytes > maxSizeBytes && files.Count > 0)
            {
                var (oldest, _, size) = files[0];
                files.RemoveAt(0);
                try
                {
                    oldest.Delete();
                    totalBytes -= size;
                    deleted++;
                    _logger.LogInformation("Deleted old clip to free space: {Name}", oldest.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete clip {Name}", oldest.Name);
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Clips cleanup: deleted {Count} file(s), folder now {SizeMb:F1} MB / {MaxMb} MB",
                    deleted, totalBytes / (1024.0 * 1024.0), maxSizeBytes / (1024.0 * 1024.0));
            }
        });
    }

    // ── Private helpers ─────────────────────────────────────────────

    private async Task<string?> RunFfmpegClipAsync(
        string ffmpeg,
        string vodPath,
        int startS,
        int endS,
        string outputPath,
        string[] extraArgs,
        int timeoutS)
    {
        var duration = endS - startS;
        var args = new List<string>
        {
            "-y",
            "-ss", startS.ToString(),
            "-i", vodPath,
            "-t", duration.ToString(),
        };
        args.AddRange(extraArgs);
        args.Add(outputPath);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        _logger.LogInformation("ffmpeg cmd: {Exe} {Args}", ffmpeg, string.Join(" ", args));

        try
        {
            using var process = new Process { StartInfo = psi };

            // Set below-normal priority after start
            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { /* ignore if we can't set priority */ }

            // Read stdout/stderr concurrently to prevent pipe buffer deadlock
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutS));
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                _logger.LogError("ffmpeg timed out after {Timeout}s", timeoutS);
                TryDeleteFile(outputPath);
                return null;
            }

            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var stderrTail = stderr.Length > 500 ? stderr[^500..] : stderr;
                _logger.LogError("ffmpeg failed (rc={Code}): {Stderr}", process.ExitCode, stderrTail);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Clip extraction failed");
            TryDeleteFile(outputPath);
            return null;
        }
    }

    private static string? FindInPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, executable + ".exe");
            if (File.Exists(fullPath)) return fullPath;
        }
        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup */ }
    }
}

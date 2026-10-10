#nullable enable

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>Outcome of one ffmpeg run. <see cref="StderrTail"/> keeps at least the last 64 KB.</summary>
internal readonly record struct FfmpegRunResult(int ExitCode, string StderrTail, bool TimedOut = false)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// ffmpeg discovery and execution shared by clip extraction, the narration mixer and the
/// transcription chunker. Every run is BelowNormal priority, bounded by a timeout, and
/// killed (whole process tree) on timeout or cancellation.
/// </summary>
internal static class FfmpegRunner
{
    /// <summary>Default stderr retention: the last 64 KB of characters.</summary>
    public const int DefaultStderrChars = 64 * 1024;

    /// <summary>
    /// Locate ffmpeg: bundled next to the exe, then PATH, then common Windows install
    /// locations (Revu's own folder, Program Files, C:\ffmpeg, scoop, WinGet).
    /// </summary>
    public static Task<string?> FindFfmpegAsync(ILogger? logger = null)
    {
        return Task.Run<string?>(() =>
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            logger?.LogDebug("Checking bundled ffmpeg: {Path}", bundled);
            if (File.Exists(bundled)) { logger?.LogInformation("Found ffmpeg (bundled): {Path}", bundled); return bundled; }

            var pathResult = FindInPath("ffmpeg");
            if (pathResult is not null) { logger?.LogInformation("Found ffmpeg in PATH: {Path}", pathResult); return pathResult; }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // WinGet packages use a glob-like path; enumerate manually.
            string? wingetFfmpeg = null;
            var wingetBase = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            try
            {
                if (Directory.Exists(wingetBase))
                {
                    wingetFfmpeg = Directory.EnumerateFiles(wingetBase, "ffmpeg.exe", SearchOption.AllDirectories)
                        .FirstOrDefault();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogDebug(ex, "WinGet ffmpeg search failed");
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
                if (File.Exists(path)) { logger?.LogInformation("Found ffmpeg: {Path}", path); return path; }
            }

            logger?.LogWarning("ffmpeg not found in any checked location");
            return null;
        });
    }

    /// <summary>
    /// Run ffmpeg with <paramref name="args"/>. Returns the exit code and stderr tail; a
    /// timeout kills the process tree and returns <c>TimedOut</c>. Cancellation kills the
    /// process tree and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<FfmpegRunResult> RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken ct,
        int maxStderrChars = DefaultStderrChars)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* the process may already have exited */ }
        // ffmpeg reads stdin for interactive keys; a closed stdin can never block it.
        try { process.StandardInput.Close(); } catch { }

        var stderr = new TailBuffer(Math.Max(DefaultStderrChars, maxStderrChars));
        var stderrTask = PumpAsync(process.StandardError, stderr);
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainQuietlyAsync(stderrTask, stdoutTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new FfmpegRunResult(-1, stderr.ToString(), TimedOut: true);
        }

        await DrainQuietlyAsync(stderrTask, stdoutTask).ConfigureAwait(false);
        return new FfmpegRunResult(process.ExitCode, stderr.ToString());
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        try { process.WaitForExit(5000); } catch { }
    }

    private static async Task DrainQuietlyAsync(Task stderrTask, Task stdoutTask)
    {
        try { await Task.WhenAll(stderrTask, stdoutTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* pipes closed by the kill */ }
    }

    private static async Task PumpAsync(StreamReader reader, TailBuffer sink)
    {
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            sink.Append(buffer, read);
        }
    }

    private static string? FindInPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var fullPath = Path.Combine(dir.Trim(), executable + ".exe");
                if (File.Exists(fullPath)) return fullPath;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>Keeps the last <c>capacity</c> characters (trims in bulk, not per append).</summary>
    private sealed class TailBuffer
    {
        private readonly int _capacity;
        private readonly StringBuilder _sb = new();
        private readonly object _gate = new();

        public TailBuffer(int capacity) => _capacity = capacity;

        public void Append(char[] chars, int count)
        {
            lock (_gate)
            {
                _sb.Append(chars, 0, count);
                if (_sb.Length > _capacity * 2) _sb.Remove(0, _sb.Length - _capacity);
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _sb.Length > _capacity ? _sb.ToString(_sb.Length - _capacity, _capacity) : _sb.ToString();
            }
        }
    }
}

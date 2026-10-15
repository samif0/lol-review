#nullable enable

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Outcome of one ffmpeg run. <see cref="StderrTail"/> keeps at least the last 64 KB. <see cref="ConsumerError"/> is
/// the exception a binary stdout consumer threw (the process was killed for it).
/// </summary>
internal readonly record struct FfmpegRunResult(int ExitCode, string StderrTail, bool TimedOut = false,
    Exception? ConsumerError = null)
{
    public bool Succeeded => !TimedOut && ExitCode == 0 && ConsumerError is null;
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
    /// Locate ffmpeg: bundled next to the exe, then PATH, then well-known per-user and
    /// Program Files install locations (see <see cref="FfmpegCandidatePaths"/>).
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

            foreach (var path in FfmpegCandidatePaths(localAppData, programFiles, userProfile, wingetFfmpeg))
            {
                if (File.Exists(path)) { logger?.LogInformation("Found ffmpeg: {Path}", path); return path; }
            }

            logger?.LogWarning("ffmpeg not found in any checked location");
            return null;
        });
    }

    /// <summary>
    /// Ordered list of well-known ffmpeg install locations probed after the bundled
    /// copy and PATH. Only directories owned by the current user or an administrator
    /// are listed (the user's profile, Program Files); folders directly under a drive
    /// root are deliberately excluded because any local account can create them.
    /// Pure function of the caller-supplied folders so it can be unit tested; a null
    /// <paramref name="wingetHit"/> is simply omitted.
    /// </summary>
    internal static IReadOnlyList<string> FfmpegCandidatePaths(
        string localAppData,
        string programFiles,
        string userProfile,
        string? wingetHit)
    {
        var candidates = new List<string>
        {
            Path.Combine(localAppData, "Revu", "ffmpeg.exe"),
            Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(userProfile, "scoop", "shims", "ffmpeg.exe"),
        };
        if (wingetHit is not null) candidates.Add(wingetHit);
        return candidates;
    }

    /// <summary>
    /// Run ffmpeg with <paramref name="args"/>. Returns the exit code and stderr tail; a
    /// timeout kills the process tree and returns <c>TimedOut</c>. Cancellation kills the
    /// process tree and throws <see cref="OperationCanceledException"/>. With
    /// <paramref name="onStdoutLine"/>, stdout is read as text and each line is handed to it
    /// (e.g. <c>-progress pipe:1</c>); with <paramref name="onStdout"/>, stdout is handed to it
    /// as a binary stream (see <see cref="RunBinaryAsync"/>); otherwise stdout is drained and discarded.
    /// </summary>
    public static async Task<FfmpegRunResult> RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken ct,
        int maxStderrChars = DefaultStderrChars,
        Action<string>? onStdoutLine = null,
        Func<Stream, CancellationToken, Task>? onStdout = null)
    {
        ct.ThrowIfCancellationRequested();
        if (onStdout is not null)
        {
            if (onStdoutLine is not null) throw new ArgumentException("Pass a line consumer or a binary consumer, not both.");
            return await RunBinaryAsync(ffmpegPath, args, timeoutSeconds, onStdout, ct, maxStderrChars).ConfigureAwait(false);
        }
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
        var stdoutTask = onStdoutLine is null
            ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null)
            : PumpLinesAsync(process.StandardOutput, onStdoutLine);

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

    /// <summary>
    /// Binary stdout mode: <paramref name="onStdout"/> reads ffmpeg's stdout (a fragmented MP4) on a token linked to
    /// the run. A consumer that throws (a bad box, a broken segment rule, a full disk) gets the process tree killed,
    /// and the result carries its exception. The consumer is always awaited to the end, bounded by the run's own
    /// timeout rather than a short drain, and its exception is never swallowed; one that returns before the end of
    /// the stream leaves the rest drained, so ffmpeg can never stall on a full pipe. A timeout or cancellation kills
    /// the process first (a blocked pipe read only ends when the pipe closes).
    /// </summary>
    private static async Task<FfmpegRunResult> RunBinaryAsync(string ffmpegPath, IReadOnlyList<string> args,
        int timeoutSeconds, Func<Stream, CancellationToken, Task> onStdout, CancellationToken ct, int maxStderrChars)
    {
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
        try { process.StandardInput.Close(); } catch { }

        var stderr = new TailBuffer(Math.Max(DefaultStderrChars, maxStderrChars));
        var stderrTask = PumpAsync(process.StandardError, stderr);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var killOnStop = run.Token.Register(static state =>
        {
            var p = (Process)state!;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }, process);

        Exception? consumerError = null;
        try
        {
            await ConsumeAsync(process.StandardOutput.BaseStream, onStdout, run.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!run.IsCancellationRequested)
        {
            consumerError = ex;
        }
        catch (Exception)
        {
            // Stopped: the kill closed the pipe under the consumer.
        }

        if (consumerError is not null || run.IsCancellationRequested)
        {
            Kill(process);
            await DrainQuietlyAsync(stderrTask, Task.CompletedTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return consumerError is not null
                ? new FfmpegRunResult(-1, stderr.ToString(), ConsumerError: consumerError)
                : new FfmpegRunResult(-1, stderr.ToString(), TimedOut: true);
        }

        // The consumer read to the end of stdout: ffmpeg is exiting.
        try
        {
            await process.WaitForExitAsync(run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainQuietlyAsync(stderrTask, Task.CompletedTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new FfmpegRunResult(-1, stderr.ToString(), TimedOut: true);
        }
        await DrainQuietlyAsync(stderrTask, Task.CompletedTask).ConfigureAwait(false);
        return new FfmpegRunResult(process.ExitCode, stderr.ToString());
    }

    private static async Task ConsumeAsync(Stream stdout, Func<Stream, CancellationToken, Task> onStdout, CancellationToken ct)
    {
        await Task.Yield();
        await onStdout(stdout, ct).ConfigureAwait(false);
        // A consumer that stopped early: drain the rest so ffmpeg never blocks on a full pipe. One that closed the
        // stream closed the pipe, which ends ffmpeg's writes as well.
        if (stdout.CanRead) await stdout.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
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

    private static async Task PumpLinesAsync(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            // A throwing consumer must not stop the pump: an unread pipe would stall ffmpeg.
            try { onLine(line); }
            catch { /* ignore consumer faults */ }
        }
    }

    private static string? FindInPath(string executable)
        => FindInPath(executable, Environment.GetEnvironmentVariable("PATH"));

    /// <summary>
    /// PATH lookup over <paramref name="pathEnv"/>. Relative entries (".", "bin",
    /// "C:tools") are skipped: they resolve against the current directory, which is not
    /// a location the user chose to trust.
    /// </summary>
    internal static string? FindInPath(string executable, string? pathEnv)
    {
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                if (!Path.IsPathFullyQualified(dir.Trim())) continue;
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

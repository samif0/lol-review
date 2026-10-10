#nullable enable

using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>Clips-folder size eviction with the 3.14 protected set (C8).</summary>
public sealed partial class ClipService
{
    /// <inheritdoc />
    public async Task EnforceFolderSizeLimitAsync(string folder, long maxSizeBytes, string? justWritten = null,
        CancellationToken ct = default)
    {
        // Defense-in-depth against a zero/negative limit (bad config write,
        // corrupted config.json): a non-positive cap would mean "delete every
        // clip". The config save clamps to >= 100 MB; anything below 1 MB
        // here can only be a bug, so refuse rather than wipe.
        if (maxSizeBytes < 1024 * 1024)
        {
            _logger.LogWarning(
                "Refusing clip folder size enforcement with implausible limit {Bytes} bytes", maxSizeBytes);
            return;
        }

        if (!Directory.Exists(folder)) return;

        IReadOnlySet<string> protectedPaths;
        try
        {
            protectedPaths = _retention is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : await _retention.GetProtectedPathsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Without the protected set we cannot tell a narrated or shared clip from an
            // old one; deleting nothing is the only safe answer.
            _logger.LogWarning(ex, "Clip eviction skipped: protected clip list unavailable");
            return;
        }

        var justWrittenFull = ClipRetentionGuard.Normalize(justWritten);

        await Task.Run(() =>
        {
            var topLevel = new DirectoryInfo(folder)
                .EnumerateFiles()
                .Where(f => ClipExtensions.Contains(f.Extension))
                .Select(f => (File: f, f.LastWriteTimeUtc, f.Length))
                .ToList();

            // Narrated renders count toward the limit but are never candidates.
            long narratedBytes = 0;
            var narratedDir = Path.Combine(folder, NarratedFolderName);
            if (Directory.Exists(narratedDir))
            {
                try
                {
                    narratedBytes = new DirectoryInfo(narratedDir)
                        .EnumerateFiles("*.mp4")
                        .Sum(f => f.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not size {Folder}", narratedDir);
                }
            }

            var totalBytes = topLevel.Sum(x => x.Length) + narratedBytes;
            if (totalBytes <= maxSizeBytes) return;

            var candidates = new List<(FileInfo File, DateTime LastWriteTimeUtc, long Length)>(topLevel.Count);
            var exemptBytes = narratedBytes;
            foreach (var x in topLevel)
            {
                if (IsExempt(x.File.FullName, justWrittenFull, protectedPaths)) exemptBytes += x.Length;
                else candidates.Add(x);
            }

            // The files that are never deleted already fill the limit: no amount of eviction
            // can get under it, and trying would delete every unprotected clip. Delete nothing.
            if (exemptBytes >= maxSizeBytes)
            {
                LogStillOverLimit(totalBytes, maxSizeBytes);
                return;
            }

            candidates.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc)); // oldest first

            int deleted = 0;
            foreach (var (file, _, size) in candidates)
            {
                if (totalBytes <= maxSizeBytes) break;
                ct.ThrowIfCancellationRequested();
                try
                {
                    file.Delete();
                    totalBytes -= size;
                    deleted++;
                    _logger.LogInformation("Deleted old clip to free space: {Name}", file.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete clip {Name}", file.Name);
                }
            }

            if (totalBytes > maxSizeBytes) LogStillOverLimit(totalBytes, maxSizeBytes);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Clips cleanup: deleted {Count} file(s), folder now {SizeMb:F1} MB / {MaxMb} MB",
                    deleted, totalBytes / (1024.0 * 1024.0), maxSizeBytes / (1024.0 * 1024.0));
            }
        }, ct).ConfigureAwait(false);
    }

    private void LogStillOverLimit(long totalBytes, long maxSizeBytes) =>
        _logger.LogWarning(
            "Clips folder still over its limit after eviction ({SizeMb:F1} MB / {MaxMb} MB): " +
            "the rest is new, narrated, shared or in use, and is never deleted automatically",
            totalBytes / (1024.0 * 1024.0), maxSizeBytes / (1024.0 * 1024.0));

    private static bool IsExempt(string fullName, string justWrittenFull, IReadOnlySet<string> protectedPaths)
    {
        var full = ClipRetentionGuard.Normalize(fullName);
        if (full.Length == 0) return true;
        if (justWrittenFull.Length > 0 && string.Equals(full, justWrittenFull, StringComparison.OrdinalIgnoreCase))
            return true;
        return protectedPaths.Contains(full);
    }
}

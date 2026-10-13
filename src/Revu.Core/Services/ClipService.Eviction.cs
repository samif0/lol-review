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

        // H5: without the retention guard there is no way to tell a Revu clip from a
        // recording or the user's own video sharing this folder, so nothing is deleted.
        if (_retention is null)
        {
            _logger.LogDebug("Clip eviction skipped: no clip records available");
            return;
        }

        IReadOnlySet<string> protectedPaths;
        IReadOnlySet<string> revuClips;
        try
        {
            protectedPaths = await _retention.GetProtectedPathsAsync(ct).ConfigureAwait(false);
            revuClips = await _retention.GetRevuClipPathsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Without the protected set we cannot tell a narrated, shared or kept clip from
            // an old one; deleting nothing is the only safe answer.
            _logger.LogWarning(ex, "Clip eviction skipped: protected clip list unavailable");
            return;
        }

        var justWrittenFull = ClipRetentionGuard.Normalize(justWritten);

        await Task.Run(() =>
        {
            var topLevel = TopLevelClipFiles(folder)
                .Select(f => (File: f, f.LastWriteTimeUtc, f.Length))
                .ToList();

            // Narrated renders count toward the limit but are never candidates.
            var narratedBytes = NarratedBytes(folder, _logger);

            var totalBytes = topLevel.Sum(x => x.Length) + narratedBytes;
            if (totalBytes <= maxSizeBytes) return;

            // Candidates are ONLY clips Revu made (vod_bookmarks.clip_path) that are not
            // protected. Every other file counts toward the limit but is never deleted.
            var candidates = new List<(FileInfo File, DateTime LastWriteTimeUtc, long Length)>(topLevel.Count);
            var exemptBytes = narratedBytes;
            foreach (var x in topLevel)
            {
                if (IsExempt(x.File.FullName, justWrittenFull, protectedPaths, revuClips)) exemptBytes += x.Length;
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

    /// <summary>
    /// The bytes the size limit counts for a clips folder, exactly the set eviction
    /// weighs: top-level clip videos plus narrated/*.mp4. No recursion, and no reparse
    /// point (junction, symlink) is followed. Used by the Settings usage meter.
    /// </summary>
    public static long MeasureFolderBytes(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        long total = 0;
        foreach (var file in TopLevelClipFiles(folder))
        {
            ct.ThrowIfCancellationRequested();
            total += file.Length;
        }
        return total + NarratedBytes(folder, null);
    }

    private static IEnumerable<FileInfo> TopLevelClipFiles(string folder) =>
        new DirectoryInfo(folder)
            .EnumerateFiles()
            .Where(f => ClipExtensions.Contains(f.Extension)
                        && (f.Attributes & FileAttributes.ReparsePoint) == 0);

    private static long NarratedBytes(string folder, ILogger? logger)
    {
        var narratedDir = new DirectoryInfo(Path.Combine(folder, NarratedFolderName));
        try
        {
            if (!narratedDir.Exists || (narratedDir.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
            return narratedDir.EnumerateFiles("*.mp4").Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Could not size {Folder}", narratedDir.FullName);
            return 0;
        }
    }

    private void LogStillOverLimit(long totalBytes, long maxSizeBytes) =>
        _logger.LogWarning(
            "Clips folder still over its limit after eviction ({SizeMb:F1} MB / {MaxMb} MB): " +
            "the rest is new, kept, narrated, shared, in use or not a Revu clip, and is never deleted automatically",
            totalBytes / (1024.0 * 1024.0), maxSizeBytes / (1024.0 * 1024.0));

    private static bool IsExempt(string fullName, string justWrittenFull, IReadOnlySet<string> protectedPaths,
        IReadOnlySet<string> revuClips)
    {
        var full = ClipRetentionGuard.Normalize(fullName);
        if (full.Length == 0) return true;
        if (!revuClips.Contains(full)) return true;
        if (justWrittenFull.Length > 0 && string.Equals(full, justWrittenFull, StringComparison.OrdinalIgnoreCase))
            return true;
        return protectedPaths.Contains(full);
    }
}

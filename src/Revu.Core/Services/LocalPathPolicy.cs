#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// The one path policy for files the sidecar reads or writes on the user's behalf.
/// A trusted path is a fully qualified LOCAL path: never a UNC share
/// (<c>\\server\share</c>), a device or extended-length form (<c>\\.\</c>, <c>\\?\</c>),
/// or an alternate data stream (a second ':'). Merely checking that a UNC path exists
/// opens an SMB connection that can hand the user's Windows credentials to that host.
/// Paths always come from Revu's own records; the renderer never chooses one.
/// </summary>
public static class LocalPathPolicy
{
    /// <summary>True with the normalized full path when <paramref name="path"/> is local.</summary>
    public static bool TryGetLocalFullPath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith(@"\/", StringComparison.Ordinal)
            || trimmed.StartsWith(@"/\", StringComparison.Ordinal)) return false;
        if (!Path.IsPathFullyQualified(trimmed)) return false;
        if (trimmed.IndexOf(':', 2) >= 0) return false;
        try
        {
            var full = Path.GetFullPath(trimmed);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            fullPath = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool IsLocal(string? path) => TryGetLocalFullPath(path, out _);

    /// <summary>
    /// True when the two folders are the same, or one contains the other. Blank or
    /// malformed paths never overlap anything.
    /// </summary>
    public static bool FoldersOverlap(string? first, string? second)
    {
        var a = NormalizeFolder(first);
        var b = NormalizeFolder(second);
        if (a.Length == 0 || b.Length == 0) return false;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var full = Path.GetFullPath(path.Trim());
            // A drive root keeps its separator ("C:\"); compare it without one.
            return Path.TrimEndingDirectorySeparator(full).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }
}

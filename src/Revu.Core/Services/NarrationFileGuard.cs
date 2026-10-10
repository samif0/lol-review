#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data;

namespace Revu.Core.Services;

/// <summary>
/// The one file-deletion guard for clip and narration files. A path read from the
/// database is never trusted as a delete target on its own:
/// <list type="bullet">
/// <item>a clip or narrated render is deleted only when it carries a clip video
/// extension (the clips folder is user-configurable, so it cannot be bounded);</item>
/// <item>a narration voice track is deleted only when its full path is inside
/// <see cref="AppDataPaths.NarrationDirectory"/> and ends in <c>.webm</c>.</item>
/// </list>
/// </summary>
public static class NarrationFileGuard
{
    private static readonly string[] ClipExtensions = [".mp4", ".webm", ".mkv", ".mov"];

    /// <summary>True for a well-formed path ending in a known clip video extension.</summary>
    public static bool IsDeletableClipPath(string? clipPath)
    {
        if (string.IsNullOrWhiteSpace(clipPath)) return false;
        string full;
        try { full = Path.GetFullPath(clipPath); }
        catch { return false; }
        var ext = Path.GetExtension(full);
        return Array.FindIndex(ClipExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) >= 0;
    }

    /// <summary>True only for a <c>.webm</c> file directly or deeper inside the narration folder.</summary>
    public static bool IsDeletableNarrationAudio(string? audioPath) =>
        IsDeletableNarrationAudio(audioPath, AppDataPaths.NarrationDirectory);

    public static bool IsDeletableNarrationAudio(string? audioPath, string narrationDirectory)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || string.IsNullOrWhiteSpace(narrationDirectory)) return false;
        string full, root;
        try
        {
            full = Path.GetFullPath(audioPath);
            root = Path.GetFullPath(narrationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return false; }
        return full.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Delete a narrated render (or any clip file) behind the clip guard. Best effort.</summary>
    public static bool TryDeleteClipFile(string? path, ILogger? logger = null)
    {
        if (!IsDeletableClipPath(path)) return false;
        return TryDelete(path!, logger);
    }

    /// <summary>Delete a narration voice track behind the narration-folder guard. Best effort.</summary>
    public static bool TryDeleteNarrationAudio(string? path, ILogger? logger = null) =>
        TryDeleteNarrationAudio(path, AppDataPaths.NarrationDirectory, logger);

    public static bool TryDeleteNarrationAudio(string? path, string narrationDirectory, ILogger? logger = null)
    {
        if (!IsDeletableNarrationAudio(path, narrationDirectory)) return false;
        return TryDelete(path!, logger);
    }

    /// <summary>Delete both files of a narration (voice track + narrated render). Best effort.</summary>
    public static void DeleteNarrationFiles(string? audioPath, string? narratedClipPath, ILogger? logger = null) =>
        DeleteNarrationFiles(audioPath, narratedClipPath, AppDataPaths.NarrationDirectory, logger);

    public static void DeleteNarrationFiles(string? audioPath, string? narratedClipPath, string narrationDirectory,
        ILogger? logger = null)
    {
        TryDeleteNarrationAudio(audioPath, narrationDirectory, logger);
        TryDeleteClipFile(narratedClipPath, logger);
    }

    private static bool TryDelete(string path, ILogger? logger)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not delete {Path}", path);
            return false;
        }
    }
}

#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;

namespace Revu.Sidecar;

/// <summary>
/// 3.14 get_vod helpers: a game's narrations by bookmark id, and a clip bookmark's file
/// path only while that file is on disk (C6: clipPath is absolute when on disk, else null).
/// </summary>
internal static class VodNarrationLookup
{
    /// <summary>The game's narrations keyed by bookmark id; empty when unavailable.</summary>
    public static async Task<Dictionary<long, ClipNarrationRecord>> LoadAsync(IClipNarrationRepository? narrations,
        long gameId, ILogger logger)
    {
        if (narrations is null) return new Dictionary<long, ClipNarrationRecord>();
        try { return await narrations.GetForGameAsync(gameId); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "VOD: narrations load failed for {GameId}", gameId);
            return new Dictionary<long, ClipNarrationRecord>();
        }
    }

    /// <summary>The clip file path only for a clip bookmark whose file is on disk.</summary>
    public static string? ClipPathOnDisk(VodBookmarkRecord b)
    {
        var hasClip = !string.IsNullOrWhiteSpace(b.ClipPath) || b.ClipStartSeconds.HasValue;
        if (!hasClip || string.IsNullOrWhiteSpace(b.ClipPath)) return null;
        try { return File.Exists(b.ClipPath) ? b.ClipPath : null; }
        catch { return null; }
    }
}

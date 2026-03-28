#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

/// <summary>
/// VOD file discovery and matching for Ascent recordings.
/// Ported from Python vod.py.
/// </summary>
public interface IVodService
{
    /// <summary>
    /// Scan a folder recursively for video files (.mp4, .mkv, .avi, .webm, .mov).
    /// Returns (path, file modified time) sorted newest first.
    /// </summary>
    Task<List<VodRecordingInfo>> FindRecordingsAsync(string? folder = null);

    /// <summary>
    /// Try to match a recording to a game based on timestamps.
    /// Primary: parse Ascent filename (MM-DD-YYYY-HH-mm) vs game start (within +/-10 min).
    /// Fallback: compare file mtime vs game end (30s grace).
    /// Returns the matched file path or null.
    /// </summary>
    string? MatchRecordingToGame(GameStats game, IReadOnlyList<VodRecordingInfo> recordings);

    /// <summary>
    /// Try to link a recording to a specific game immediately.
    /// Returns true when the game ends up with a linked VOD.
    /// </summary>
    Task<bool> TryLinkRecordingAsync(GameStats game, string? folder = null);

    /// <summary>
    /// Scan for recordings and auto-match them to unlinked games.
    /// Returns the count of newly matched VODs.
    /// </summary>
    Task<int> AutoMatchRecordingsAsync();
}

/// <summary>Metadata about a discovered recording file.</summary>
public record VodRecordingInfo(
    string Path,
    string Name,
    long Size,
    double Mtime,
    double? StartTs,
    string MtimeStr);

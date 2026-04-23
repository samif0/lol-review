#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Clip extraction from VODs using ffmpeg and folder size management.
/// Ported from Python clips.py.
/// </summary>
public interface IClipService
{
    /// <summary>
    /// Locate the ffmpeg executable. Searches: bundled (next to exe), PATH,
    /// and common Windows install paths.
    /// </summary>
    Task<string?> FindFfmpegAsync();

    /// <summary>
    /// Extract a clip from a VOD file using ffmpeg.
    /// Stage 1: stream copy (fast, 60s timeout).
    /// Stage 2: re-encode fallback (ultrafast, 180s timeout).
    /// Below-normal process priority.
    /// Filename: {champion}_{start_mm-ss}_{duration}s_{timestamp}.{ext}
    /// Returns the output file path, or null on failure.
    /// </summary>
    Task<string?> ExtractClipAsync(
        string vodPath,
        int startS,
        int endS,
        string champion,
        string? outputFolder = null);

    /// <summary>
    /// Delete oldest clips until the folder is under the specified max size.
    /// </summary>
    Task EnforceFolderSizeLimitAsync(string folder, long maxSizeBytes);
}

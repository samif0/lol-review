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

    /// <summary>
    /// Losslessly remux a VOD into a clean, Media-Foundation-friendly MP4 so it
    /// plays in MediaPlayerElement. Some recordings (e.g. an Ascent capture that
    /// didn't close cleanly) finalize with a layout — duplicate moov atoms, no
    /// faststart index — that ffmpeg reads fine but Windows Media Foundation
    /// renders as a black screen. Stream-copies every track (<c>-c copy -map 0</c>)
    /// with <c>+faststart</c>, so it's fast and quality-lossless.
    /// </summary>
    /// <param name="vodPath">Source video that failed to play.</param>
    /// <param name="outputPath">
    /// Destination for the repaired file. If null, a sibling
    /// <c>&lt;name&gt;.revufix.mp4</c> next to the source is used.
    /// </param>
    /// <returns>The repaired file path, or null if repair failed.</returns>
    Task<string?> RemuxForPlaybackAsync(
        string vodPath,
        string? outputPath = null,
        CancellationToken cancellationToken = default);
}

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
    /// Input times are game seconds; native recording timing is translated once here.
    /// Stage 1: stream copy (timeout <see cref="Constants.GameConstants.CopyTimeoutSeconds"/>).
    /// Stage 2: re-encode fallback (ultrafast, 180s timeout), only for clips of at most
    /// <see cref="Constants.GameConstants.ReencodeFallbackMaxClipSeconds"/> seconds; a longer
    /// clip whose stream copy fails throws <see cref="ClipExtractionException"/>.
    /// Below-normal process priority. Cancelling <paramref name="ct"/> kills ffmpeg, deletes
    /// partial output and throws <see cref="OperationCanceledException"/>.
    /// After success the clips folder size limit is enforced with the new file exempt.
    /// Filename: {champion}_{start_mm-ss}_{duration}s_{timestamp}.{ext}
    /// Returns the output file path, or null on failure.
    /// </summary>
    Task<string?> ExtractClipAsync(
        string vodPath,
        int startS,
        int endS,
        string champion,
        string? outputFolder = null,
        CancellationToken ct = default);

    /// <summary>
    /// Delete oldest clips until the folder is under the specified max size. Counts the
    /// top-level clip files plus <c>narrated\*.mp4</c>; only top-level files are candidates,
    /// never <paramref name="justWritten"/> and never a protected path (narration sources and
    /// renders, shared clips, files pinned by a running share).
    /// </summary>
    Task EnforceFolderSizeLimitAsync(string folder, long maxSizeBytes, string? justWritten = null,
        CancellationToken ct = default);
}

/// <summary>Why <see cref="IClipService.ExtractClipAsync"/> failed in a way the caller must explain.</summary>
public enum ClipExtractionFailure
{
    /// <summary>The stream copy failed and the clip is too long for the re-encode fallback.</summary>
    LongClipCopyFailed,
}

/// <summary>A clip extraction failure with a distinguishable reason.</summary>
public sealed class ClipExtractionException : Exception
{
    public ClipExtractionFailure Reason { get; }

    public ClipExtractionException(ClipExtractionFailure reason, string message) : base(message)
    {
        Reason = reason;
    }
}

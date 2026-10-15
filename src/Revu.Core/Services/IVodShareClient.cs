#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// What to ask the proxy for at VOD upload init. Quality is the wire string "1080p" or "720p";
/// Fps (60 or 30) is sent alongside it, and a proxy that predates it ignores it.
/// </summary>
public sealed record VodUploadRequest(string Quality, int DurationSeconds, int Fps = 30);

/// <summary>A finished VOD share: the slug, the revu.lol/v/&lt;id&gt; link and when it expires (unix seconds).</summary>
public sealed record VodUploadResult(string Id, string Url, long ExpiresAt);

/// <summary>
/// A VOD upload the proxy accepted at init: the slug, the revu.lol/v/&lt;id&gt; link and when the
/// share will expire (unix seconds). The link works from here on: until the upload completes,
/// the watch page shows its progress and starts the game once it is ready.
/// </summary>
public sealed record VodUploadStarted(string Id, string Url, long ExpiresAt);

/// <summary>
/// An upload that started while its share copy was being encoded was given up by the encoding
/// side (the encode failed or restarted, or the copy did not fit the reserved size). The remote
/// upload has been deleted (best effort); the caller decides what comes next.
/// </summary>
public sealed class VodCopyAbandonedException() : Exception("The share copy changed while it was uploading.");

/// <summary>Bytes the hourly purge frees at <see cref="At"/> (all kinds, and the VOD subset).</summary>
public sealed record VodSharePoolRelease(long At, long Bytes, long VodBytes);

/// <summary>The proxy's account-wide storage pool as GET /vod-shares/status reports it.</summary>
public sealed record VodSharePool(long TotalBudgetBytes, long VodBudgetBytes, long UsedBytes, long VodUsedBytes,
    long FreeBytes, IReadOnlyList<VodSharePoolRelease> Releases)
{
    /// <summary>
    /// When a VOD of <paramref name="size"/> bytes fits: <paramref name="now"/> when it fits
    /// already, else the first release time after which both the total and the VOD budgets
    /// have room, else null. Mirrors <c>fitsAt</c> in proxy/src/storage-budget.ts exactly.
    /// </summary>
    public long? FitsAt(long size, long now)
    {
        var freeTotal = TotalBudgetBytes - UsedBytes;
        var freeVod = VodBudgetBytes - VodUsedBytes;
        if (Math.Min(freeTotal, freeVod) >= size) return now;
        foreach (var release in (Releases ?? Array.Empty<VodSharePoolRelease>()).OrderBy(r => r.At))
        {
            freeTotal += release.Bytes;
            freeVod += release.VodBytes;
            if (Math.Min(freeTotal, freeVod) >= size) return release.At;
        }
        return null;
    }
}

/// <summary>Per-account VOD limits the proxy enforces (present only for an allowed account).</summary>
public sealed record VodShareServerLimits(long MaxBytes, int MaxSeconds, long TtlSeconds, int MaxLive, int MaxPending,
    int MaxStartsPerDay, long PartSize, int MarkersMaxBytes, int NotesMaxBytes, int DailyPlayCap);

/// <summary>The caller's current VOD usage (idle uploads excluded).</summary>
public sealed record VodShareServerUsage(int Live, int Pending, int StartsToday, long DayResetsAt);

/// <summary>
/// One of the caller's own live VOD shares (status ready or uploading). Format is "mp4" or "hls" (4.4); an HLS share
/// also reports its committed segments and whether it is paused.
/// </summary>
public sealed record VodShareServerShare(string Id, string Status, string? Quality, long SizeBytes, long CreatedAt, long ExpiresAt,
    string Format = "mp4", int? Segments = null, bool Paused = false);

/// <summary>
/// The proxy's HLS settings (4.4, GET /vod-shares/status <c>hls</c>): whether this account may start an HLS share
/// now, and the limits its segments, inits and runs must keep.
/// </summary>
public sealed record VodShareHlsInfo(bool Enabled, int SegmentSeconds, int TargetDuration, long MaxSegmentBytes,
    int InitMaxBytes, int PlayableMs, int MaxRuns);

/// <summary>
/// GET /vod-shares/status. <see cref="Supported"/> is false for a proxy without VOD routes
/// (then nothing else is meaningful); limits, usage and pool are null unless allowed. Hls is null for a
/// proxy without HLS shares.
/// </summary>
public sealed record VodShareServerStatus(bool Supported, bool Allowed, bool Sharing, bool Serving, long Now,
    VodShareServerLimits? Limits, VodShareServerUsage? Usage, VodSharePool? Pool,
    IReadOnlyList<VodShareServerShare> Shares, VodShareHlsInfo? Hls = null);

/// <summary>
/// The MP4 multipart row an older proxy answered an HLS init with (it ignores <c>format</c>): the upload continues as
/// today's pipelined MP4 on this row, so no second daily start is spent.
/// </summary>
public sealed record VodMp4Reservation(string Id, long PartSize, int PartCount, long DeclaredSize, long ShareExpiresAt, string Url);

/// <summary>
/// What an HLS init returned: the started share (its link works at once) and either the HLS upload session or, from
/// an older proxy, an MP4 reservation for today's path.
/// </summary>
public sealed record VodHlsStart(VodUploadStarted Started, IVodHlsUpload? Session, VodMp4Reservation? Mp4);

/// <summary>
/// The proxy side of full-game sharing (4.2): status, the multipart upload of the share copy,
/// and the markers document. Implemented by <see cref="ClipUploadService"/>; a VOD's remote
/// delete is the kind-agnostic <see cref="IClipUploadService.DeleteAsync"/>.
/// </summary>
public interface IVodShareClient
{
    /// <summary>GET /vod-shares/status (10 s). Throws <see cref="ClipUploadException"/> with Code
    /// <c>unauthorized</c> or <c>network</c>; an old proxy returns Supported = false.</summary>
    Task<VodShareServerStatus> GetVodShareStatusAsync(string sessionToken, CancellationToken ct = default);

    /// <summary>
    /// Multipart upload of a share copy (init, 16 MiB parts, complete), whatever its size.
    /// <paramref name="onStarted"/> runs once init succeeded, before any part goes up: the
    /// remote upload exists from then on, and its link already works. Throws
    /// <see cref="ClipUploadException"/> with Code set; a failed or cancelled upload is deleted
    /// remotely (best effort).
    /// </summary>
    Task<VodUploadResult> UploadVodAsync(string filePath, string sessionToken, VodUploadRequest request,
        IProgress<ClipUploadProgress>? progress = null, Action<VodUploadStarted>? onStarted = null,
        CancellationToken ct = default);

    /// <summary>
    /// The same upload while the share copy is still being encoded (4.3): init reserves
    /// <paramref name="declaredSize"/> bytes at once (and reports through
    /// <paramref name="onStarted"/>), and each part goes up as soon as the file has grown past
    /// it. <paramref name="written"/> completes true once the file is final at exactly
    /// <paramref name="declaredSize"/> bytes; false (or a fault) abandons the upload, which then
    /// throws <see cref="VodCopyAbandonedException"/> after deleting the remote (its link stops
    /// working). Other failures throw <see cref="ClipUploadException"/> with Code set.
    /// </summary>
    Task<VodUploadResult> UploadVodWhileWritingAsync(string filePath, long declaredSize, Task<bool> written,
        string sessionToken, VodUploadRequest request, IProgress<ClipUploadProgress>? progress = null,
        Action<VodUploadStarted>? onStarted = null, CancellationToken ct = default);

    /// <summary>PUT /vod-shares/{id}/markers (30 s). Throws <see cref="ClipUploadException"/> with Code.</summary>
    Task PutVodMarkersAsync(string id, string sessionToken, string markersJson, CancellationToken ct = default);

    /// <summary>
    /// 4.4: POST /vod-shares/uploads with <c>format=hls</c> (30 s), reserving <paramref name="declaredSize"/> bytes.
    /// Returns an HLS upload session, or the MP4 reservation an older proxy answered with. Throws
    /// <see cref="ClipUploadException"/> with Code (<c>hls_unavailable</c> when HLS is off for this account).
    /// </summary>
    Task<VodHlsStart> StartVodHlsUploadAsync(string sessionToken, VodUploadRequest request, long declaredSize,
        CancellationToken ct = default);

    /// <summary>
    /// Today's pipelined MP4 upload (see <see cref="UploadVodWhileWritingAsync(string, long, Task{bool}, string,
    /// VodUploadRequest, IProgress{ClipUploadProgress}?, Action{VodUploadStarted}?, CancellationToken)"/>) on a row
    /// an HLS init already reserved: no second init.
    /// </summary>
    Task<VodUploadResult> UploadVodWhileWritingAsync(VodMp4Reservation reserved, string filePath, Task<bool> written,
        string sessionToken, IProgress<ClipUploadProgress>? progress = null, CancellationToken ct = default);
}

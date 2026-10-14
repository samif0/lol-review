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

/// <summary>One of the caller's own live VOD shares (status ready or uploading).</summary>
public sealed record VodShareServerShare(string Id, string Status, string? Quality, long SizeBytes, long CreatedAt, long ExpiresAt);

/// <summary>
/// GET /vod-shares/status. <see cref="Supported"/> is false for a proxy without VOD routes
/// (then nothing else is meaningful); limits, usage and pool are null unless allowed.
/// </summary>
public sealed record VodShareServerStatus(bool Supported, bool Allowed, bool Sharing, bool Serving, long Now,
    VodShareServerLimits? Limits, VodShareServerUsage? Usage, VodSharePool? Pool,
    IReadOnlyList<VodShareServerShare> Shares);

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
    /// Throws <see cref="ClipUploadException"/> with Code set; a failed or cancelled upload is
    /// deleted remotely (best effort).
    /// </summary>
    Task<VodUploadResult> UploadVodAsync(string filePath, string sessionToken, VodUploadRequest request,
        IProgress<ClipUploadProgress>? progress = null, Action<string>? onRemoteIdAssigned = null,
        CancellationToken ct = default);

    /// <summary>
    /// The same upload while the share copy is still being encoded (4.3): init reserves
    /// <paramref name="declaredSize"/> bytes at once, and each part goes up as soon as the file
    /// has grown past it. <paramref name="written"/> completes true once the file is final at
    /// exactly <paramref name="declaredSize"/> bytes; false (or a fault) abandons the upload,
    /// which then throws <see cref="VodCopyAbandonedException"/> after deleting the remote.
    /// Other failures throw <see cref="ClipUploadException"/> with Code set.
    /// </summary>
    Task<VodUploadResult> UploadVodWhileWritingAsync(string filePath, long declaredSize, Task<bool> written,
        string sessionToken, VodUploadRequest request, IProgress<ClipUploadProgress>? progress = null,
        Action<string>? onRemoteIdAssigned = null, CancellationToken ct = default);

    /// <summary>PUT /vod-shares/{id}/markers (30 s). Throws <see cref="ClipUploadException"/> with Code.</summary>
    Task PutVodMarkersAsync(string id, string sessionToken, string markersJson, CancellationToken ct = default);
}

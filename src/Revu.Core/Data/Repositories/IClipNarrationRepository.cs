#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>The <c>clip_narrations.transcript_status</c> values (C6 NarrationDto).</summary>
public static class TranscriptStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string NeedsLogin = "needs_login";
    public const string Quota = "quota";
}

/// <summary>
/// 3.14 narrated clips: CRUD for <c>clip_narrations</c>, keyed by bookmark id.
/// Every method tolerates a database that has no clip_narrations table yet (reads
/// return empty, writes create the table first), the same way VodRepository tolerates
/// a missing share_url column. Timestamps are unix seconds.
/// </summary>
public interface IClipNarrationRepository
{
    Task<ClipNarrationRecord?> GetAsync(long bookmarkId);

    Task<Dictionary<long, ClipNarrationRecord>> GetForGameAsync(long gameId);

    /// <summary>
    /// Insert or replace the narration of <paramref name="record"/>.BookmarkId. Keeps
    /// created_at, bumps transcript_generation, writes the record's transcript_status and
    /// clears the transcript fields. Returns the PREVIOUS record, or null.
    /// </summary>
    Task<ClipNarrationRecord?> UpsertAsync(ClipNarrationRecord record);

    /// <summary>
    /// Store a re-render. With <paramref name="resetTranscript"/> it also bumps the
    /// generation, sets <paramref name="statusIfReset"/> and clears the transcript.
    /// Returns the previous record, or null when there is no narration.
    /// </summary>
    Task<ClipNarrationRecord?> UpdateMixAsync(long bookmarkId, string narratedClipPath, int offsetMs,
        double gameVolume, double narrationVolume, bool duck, bool resetTranscript, string statusIfReset);

    /// <summary>Bump the generation, set the status, clear the transcript. False when no row.</summary>
    Task<bool> ResetTranscriptAsync(long bookmarkId, string status);

    /// <summary>pending to processing, only for this generation. True when this caller won the claim.</summary>
    Task<bool> TryClaimTranscriptAsync(long bookmarkId, long generation);

    /// <summary>
    /// Compare-and-set the transcript result: applies only while the row is still
    /// <c>processing</c> at <paramref name="generation"/>. False means the result is stale.
    /// </summary>
    Task<bool> TrySetTranscriptAsync(long bookmarkId, long generation, string status,
        string language, string json, string error);

    Task SetTranscriptPushedSlugAsync(long bookmarkId, string slug);

    /// <summary>Delete the narration row. Returns the deleted record, or null.</summary>
    Task<ClipNarrationRecord?> DeleteAsync(long bookmarkId);

    Task<IReadOnlyList<ClipNarrationRecord>> ListByTranscriptStatusAsync(params string[] statuses);

    /// <summary>Startup: rows left <c>processing</c> by a previous run go back to <c>pending</c>.</summary>
    Task<int> ResetProcessingToPendingAsync();

    Task<IReadOnlyList<string>> ListAudioPathsAsync();

    /// <summary>Read-only: how many rows have no bookmark (lets the caller back up before deleting them).</summary>
    Task<int> CountOrphansAsync();

    /// <summary>Delete rows whose bookmark no longer exists; returns them so the caller can delete files.</summary>
    Task<IReadOnlyList<ClipNarrationRecord>> DeleteOrphansAsync();

    /// <summary>Every non-empty source_clip_path and narrated_clip_path (clip eviction protection).</summary>
    Task<IReadOnlyList<string>> ListProtectedClipPathsAsync();
}

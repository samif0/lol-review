#nullable enable

namespace Revu.Sidecar;

// ── Full-game (VOD) sharing, 4.2 (contract 7.5 to 7.7) ───────────────────────

/// <summary>POST /api/vod-share/start. Quality "1080p" (default) or "720p"; IncludeNotes defaults to false.</summary>
internal sealed record VodShareStartBody(long GameId, string? Quality, bool? IncludeNotes);

/// <summary>POST /api/vod-share/cancel and /api/vod-share/stop.</summary>
internal sealed record VodShareGameBody(long GameId);

/// <summary>The game's linked recording as the share sheet needs it.</summary>
public sealed record VodShareRecordingDto(bool Available, bool BuiltIn, int? DurationSeconds, bool TooLong);

/// <summary>Estimated share copy sizes (bytes).</summary>
public sealed record VodShareEstimatesDto(long P1080Bytes, long P720Bytes);

/// <summary>The proxy pool for this game's estimates. *At = when it would fit, when it does not fit now.</summary>
public sealed record VodSharePoolDto(long FreeBytes, bool Fits1080, bool Fits720, long? Fits1080At, long? Fits720At);

/// <summary>The account's VOD usage against its limits.</summary>
public sealed record VodShareLimitsDto(int Live, int MaxLive, int Pending, int StartsToday, int MaxStartsPerDay,
    long DayResetsAt);

/// <summary>The game's running job, or its last finished one since the sidecar started.</summary>
public sealed record VodShareJobDto(string State, string Phase, int Percent, string? Quality, string? QualityReason,
    string? ErrorCode, string? Error, long? RetryAt, bool NeedsLogin);

/// <summary>A live share of this game (the store entry without its game id).</summary>
public sealed record VodShareLinkDto(string Id, string Url, string Quality, bool IncludeNotes, long SizeBytes,
    long CreatedAt, long ExpiresAt, bool MarkersAttached)
{
    public static VodShareLinkDto From(VodShareEntry e) =>
        new(e.Id, e.Url, e.Quality, e.IncludeNotes, e.SizeBytes, e.CreatedAt, e.ExpiresAt, e.MarkersAttached);
}

/// <summary>GET /api/vod-share/status?gameId=N.</summary>
public sealed record VodShareStatusDto(
    long GameId,
    bool SignedIn,
    bool Allowed,
    bool SharingOn,
    bool Reachable,
    VodShareRecordingDto Recording,
    VodShareEstimatesDto? Estimates,
    VodSharePoolDto? Pool,
    VodShareLimitsDto? Limits,
    long TtlSeconds,
    VodShareJobDto? Job,
    VodShareLinkDto? Share,
    long? BusyGameId);

/// <summary>
/// POST /api/vod-share/start reply (HTTP status + JSON body, like <see cref="ApiReply"/>). A
/// refusal is HTTP 200 <c>{ ok: false, errorCode, error, retryAt, needsLogin, busyGameId }</c>.
/// </summary>
public sealed record VodShareStartReply(int Status, object Body)
{
    public static VodShareStartReply Refuse(string errorCode, string error, long? retryAt = null,
        bool needsLogin = false, long? busyGameId = null) =>
        new(200, new { ok = false, errorCode, error, retryAt, needsLogin, busyGameId });

    public static VodShareStartReply Accepted(string quality, string? qualityReason) =>
        new(200, new { ok = true, accepted = true, quality, qualityReason });

    public static VodShareStartReply AlreadyRunning() =>
        new(200, new { ok = true, accepted = true, alreadyRunning = true });

    public static VodShareStartReply AlreadyShared(VodShareLinkDto share) =>
        new(200, new { ok = true, alreadyShared = true, share });

    public static VodShareStartReply BadRequest(string error) => new(400, new { ok = false, error });
}

/// <summary>POST /api/vod-share/stop reply.</summary>
public sealed record VodShareStopReply(bool Ok, bool RemoteDeleted, bool Queued);

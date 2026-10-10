#nullable enable

namespace Revu.Core.Services;

/// <summary>Result of a successful clip upload.</summary>
/// <param name="Id">Short public slug (the <c>&lt;id&gt;</c> in revu.lol/&lt;id&gt;).</param>
/// <param name="Url">Full public watch URL, e.g. https://revu.lol/abc1234.</param>
/// <param name="ExpiresAt">Unix seconds when the clip auto-expires (3-day retention).</param>
public sealed record ClipUploadResult(string Id, string Url, long ExpiresAt);

/// <summary>
/// Upload progress. <see cref="Phase"/> is <c>uploading</c> (bytes moving) or
/// <c>finishing</c> (multipart complete).
/// </summary>
public sealed record ClipUploadProgress(string Phase, long SentBytes, long TotalBytes);

/// <summary>
/// Uploads a local clip file to the Revu backend so it can be shared publicly
/// via revu.lol/&lt;id&gt;. Requires a logged-in session token (Path B auth).
/// </summary>
public interface IClipUploadService
{
    /// <summary>
    /// Upload a clip file. At or below 95 MiB it is one legacy <c>POST /clips</c>; above it
    /// is an R2 multipart upload (init, 16 MiB parts, complete). Throws
    /// <see cref="ClipUploadException"/> on any failure with a user-displayable message. A
    /// failed or cancelled multipart upload is DELETEd remotely (best effort, not bound to
    /// <paramref name="ct"/>).
    /// </summary>
    /// <param name="filePath">Absolute path to the local clip (.mp4 / .webm).</param>
    /// <param name="sessionToken">The logged-in session token from config.</param>
    /// <param name="title">Optional uploader caption (no account data).</param>
    /// <param name="champion">Optional champion tag.</param>
    /// <param name="durationSeconds">Clip length, shown on the watch page (required for multipart).</param>
    /// <param name="progress">Optional byte progress reporter.</param>
    /// <param name="narrated">True when the file is a narrated render.</param>
    /// <param name="onRemoteIdAssigned">Called with the remote id right after a multipart init,
    /// so the caller can persist it for cleanup.</param>
    Task<ClipUploadResult> UploadAsync(
        string filePath,
        string sessionToken,
        string? title = null,
        string? champion = null,
        int? durationSeconds = null,
        IProgress<ClipUploadProgress>? progress = null,
        bool narrated = false,
        Action<string>? onRemoteIdAssigned = null,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a previously-uploaded clip. Owner-only on the server; best-effort
    /// (swallows network errors, bounded by its own 10 s timeout). Returns true when the
    /// remote copy is gone (2xx or 404). False when either argument is empty.
    /// </summary>
    Task<bool> DeleteAsync(string clipId, string sessionToken, CancellationToken ct = default);

    /// <summary><c>PUT /clips/{id}/transcript</c> with a C2 document. Throws on failure.</summary>
    Task PutTranscriptAsync(string clipId, string sessionToken, TranscriptDocument doc, CancellationToken ct = default);
}

/// <summary>Failure from the clip upload flow, with a user-displayable message.</summary>
public sealed class ClipUploadException : Exception
{
    /// <summary>
    /// True when the failure was an auth rejection (HTTP 401/403) — the stored
    /// session is missing, expired, or not allowed. The caller can use this to
    /// re-prompt for login rather than just showing the message.
    /// </summary>
    public bool Unauthorized { get; }

    /// <summary>
    /// True only for a 5xx, network or timeout failure on the legacy path or at multipart
    /// init: the whole share may be retried. Part and complete failures were already
    /// retried and are terminal.
    /// </summary>
    public bool Retryable { get; }

    /// <summary>
    /// True when the server says the shared clip no longer exists (404 on a transcript PUT:
    /// expired, deleted, or not this account's). Retrying the same clip cannot succeed.
    /// </summary>
    public bool Gone { get; init; }

    public ClipUploadException(string message, bool unauthorized = false, bool retryable = false) : base(message)
    {
        Unauthorized = unauthorized;
        Retryable = retryable;
    }

    public ClipUploadException(string message, Exception inner, bool unauthorized = false, bool retryable = false)
        : base(message, inner)
    {
        Unauthorized = unauthorized;
        Retryable = retryable;
    }
}

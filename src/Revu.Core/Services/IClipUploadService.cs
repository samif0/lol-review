#nullable enable

namespace Revu.Core.Services;

/// <summary>Result of a successful clip upload.</summary>
/// <param name="Id">Short public slug (the <c>&lt;id&gt;</c> in revu.lol/&lt;id&gt;).</param>
/// <param name="Url">Full public watch URL, e.g. https://revu.lol/abc1234.</param>
/// <param name="ExpiresAt">Unix seconds when the clip auto-expires (30-day retention).</param>
public sealed record ClipUploadResult(string Id, string Url, long ExpiresAt);

/// <summary>
/// Uploads a local clip file to the Revu backend so it can be shared publicly
/// via revu.lol/&lt;id&gt;. Requires a logged-in session token (Path B auth).
/// </summary>
public interface IClipUploadService
{
    /// <summary>
    /// POST a clip file to <c>/clips</c>. Throws <see cref="ClipUploadException"/>
    /// on any failure with a user-displayable message.
    /// </summary>
    /// <param name="filePath">Absolute path to the local clip (.mp4 / .webm).</param>
    /// <param name="sessionToken">The logged-in session token from config.</param>
    /// <param name="title">Optional uploader caption (no account data).</param>
    /// <param name="champion">Optional champion tag.</param>
    /// <param name="durationSeconds">Optional clip length, shown on the watch page.</param>
    /// <param name="progress">Optional 0..1 upload progress reporter.</param>
    Task<ClipUploadResult> UploadAsync(
        string filePath,
        string sessionToken,
        string? title = null,
        string? champion = null,
        int? durationSeconds = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a previously-uploaded clip. Owner-only on the server; best-effort
    /// (swallows network errors). No-op if either argument is empty.
    /// </summary>
    Task DeleteAsync(string clipId, string sessionToken, CancellationToken ct = default);
}

/// <summary>Failure from the clip upload flow, with a user-displayable message.</summary>
public sealed class ClipUploadException : Exception
{
    public ClipUploadException(string message) : base(message) { }
    public ClipUploadException(string message, Exception inner) : base(message, inner) { }
}

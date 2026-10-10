#nullable enable

using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>Share-link helpers shared by clip delete, game delete and narration changes.</summary>
internal static class ClipShareLinks
{
    /// <summary>
    /// The public slug in a revu.lol/&lt;slug&gt; share URL (the last non-empty path
    /// segment), or "" when the URL is empty or unparseable.
    /// </summary>
    public static string SlugFromUrl(string? shareUrl)
    {
        if (string.IsNullOrWhiteSpace(shareUrl)) return "";
        var s = shareUrl.Trim().TrimEnd('/');
        var slash = s.LastIndexOf('/');
        var slug = slash >= 0 ? s[(slash + 1)..] : s;
        // Strip any query/fragment the URL might carry.
        var cut = slug.IndexOfAny(['?', '#']);
        if (cut >= 0) slug = slug[..cut];
        return slug.Trim();
    }

    /// <summary>
    /// Delete the remote copy now when signed in (bounded, never bound to a request token);
    /// otherwise, or when the delete did not go through, queue the slug for the next
    /// signed-in drain. Returns true when the remote copy is confirmed gone. Background
    /// callers pass a token that ends the attempt at shutdown (the slug stays queued).
    /// </summary>
    public static async Task<bool> DeleteOrQueueAsync(string slug, IConfigService config,
        IClipUploadService clips, RemoteClipCleanupStore cleanup, ILogger log, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return false;
        var session = await SessionAsync(config).ConfigureAwait(false);
        if (session.SignedIn && !ct.IsCancellationRequested)
        {
            try
            {
                if (await clips.DeleteAsync(slug, session.Token, ct).ConfigureAwait(false))
                    return true;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Remote delete of {Slug} failed; queued for later", slug);
            }
        }
        cleanup.Add(slug);
        return false;
    }

    /// <summary>Signed in = the stored session token is present and unexpired (Token is "" otherwise).</summary>
    public static async Task<(bool SignedIn, string Token)> SessionAsync(IConfigService config)
    {
        var cfg = await config.LoadAsync().ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedIn = !string.IsNullOrWhiteSpace(cfg.RiotSessionToken) && cfg.RiotSessionExpiresAt > now;
        return (signedIn, signedIn ? cfg.RiotSessionToken : "");
    }
}

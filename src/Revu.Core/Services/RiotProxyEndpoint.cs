#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// The Riot proxy endpoint. Users never configure it. The only override is
/// <c>REVU_PROXY_BASE_URL</c>, honored solely for a loopback http URL (a local
/// <c>wrangler dev</c>); anything else falls back to the production Worker.
/// </summary>
public static class RiotProxyEndpoint
{
    public const string ProductionBaseUrl = "https://revu-proxy.lol-review.workers.dev";

    /// <summary>The proxy base URL every client (auth, match, clips, transcripts, VOD) uses.</summary>
    public static string BaseUrl { get; } = Resolve();

    private static string Resolve() => Resolve(Environment.GetEnvironmentVariable("REVU_PROXY_BASE_URL"));

    /// <summary>
    /// <paramref name="overrideValue"/> (trailing slash trimmed) when it is an absolute http URL
    /// on localhost, 127.0.0.1 or [::1] with no user info, path, query or fragment; otherwise
    /// the production URL.
    /// </summary>
    internal static string Resolve(string? overrideValue)
    {
        if (string.IsNullOrWhiteSpace(overrideValue)) return ProductionBaseUrl;
        var raw = overrideValue.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return ProductionBaseUrl;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return ProductionBaseUrl;
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return ProductionBaseUrl;
        if (uri.AbsolutePath != "/") return ProductionBaseUrl;
        // Raw text checks too: Uri normalizes away an empty "?" or "#" and "user@" spellings.
        if (raw.IndexOfAny(['?', '#', '@', '\\']) >= 0) return ProductionBaseUrl;
        var host = uri.Host;
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "[::1]";
        if (!loopback) return ProductionBaseUrl;
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}

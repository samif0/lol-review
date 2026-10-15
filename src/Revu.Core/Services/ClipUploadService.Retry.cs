#nullable enable

using System.Diagnostics;
using System.Net.Http;

namespace Revu.Core.Services;

/// <summary>
/// The retry rules uploads share: clip parts get a fixed number of attempts; full-game uploads (4.3 parts, 4.4 HLS
/// segments) keep that limit only for attempts that reach the proxy, and retry connection failures with backoff for
/// <see cref="VodPartOutageWindow"/>.
/// </summary>
public sealed partial class ClipUploadService
{
    /// <summary>Delays between the 4 attempts of one part (tests shorten these).</summary>
    internal TimeSpan[] PartRetryDelays { get; set; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>
    /// Full-game (VOD) parts and segments only: how long one keeps retrying while the network is down
    /// (connection failures never reach the proxy, so they don't count against its attempt cap). Attempts
    /// that reach the proxy keep the clip limit of 4. Kept well under the proxy's 30-minute idle sweep.
    /// Tests shorten it.
    /// </summary>
    internal TimeSpan VodPartOutageWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Full-game parts: backoff between connection-failure retries, the last one repeating (tests shorten these).</summary>
    internal TimeSpan[] VodPartOutageDelays { get; set; } =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60),
    ];

    /// <summary>The full-game retry state for one part or segment, with this service's delays.</summary>
    internal UploadRetry VodRetry() => new(PartRetryDelays, VodPartOutageDelays, VodPartOutageWindow);

    /// <summary>
    /// A send that failed before any request reached the proxy (no DNS, no route, refused,
    /// TLS never set up). Anything else may have been counted by the proxy's part cap.
    /// </summary>
    internal static bool IsConnectFailure(Exception ex) =>
        ex is HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError,
        };
}

/// <summary>
/// One full-game upload's retry state: an attempt that reached the proxy (a 5xx, a timeout after sending) uses one of
/// the server attempts; a connection failure retries with backoff until the outage window, which starts at the first
/// failure of a streak, runs out. Each call answers the delay before the next attempt, or null when none is left.
/// </summary>
internal sealed class UploadRetry(TimeSpan[] serverDelays, TimeSpan[] outageDelays, TimeSpan outageWindow)
{
    private int _server;
    private int _outage;
    private long _outageSince;

    public int ServerAttempts => _server;

    /// <summary>An attempt that reached the proxy failed.</summary>
    public TimeSpan? Server()
    {
        _outage = 0;
        return ++_server <= serverDelays.Length ? serverDelays[_server - 1] : null;
    }

    /// <summary>A send that never reached the proxy failed.</summary>
    public TimeSpan? Outage()
    {
        if (_outage++ == 0) _outageSince = Stopwatch.GetTimestamp();
        return Stopwatch.GetElapsedTime(_outageSince) < outageWindow
            ? outageDelays[Math.Min(_outage, outageDelays.Length) - 1]
            : null;
    }

    /// <summary>A failed send: an outage when it never reached the proxy, else a server attempt.</summary>
    public TimeSpan? After(Exception ex) => ClipUploadService.IsConnectFailure(ex) ? Outage() : Server();
}

#nullable enable

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// v2.15.8: minimal client for the Cloudflare Worker's <c>/match/{matchId}</c>
/// endpoint, which forwards to Riot's Match-V5 API. The Worker injects the
/// server-side <c>X-Riot-Token</c>; we just attach our session bearer.
///
/// Backfill-only — not wired into the live game ingest path.
/// </summary>
public interface IRiotMatchClient
{
    Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default);

    /// <summary>
    /// v2.18: Match-V5 timeline — per-minute participant frames (gold, CS,
    /// XP, position) and positioned events. Null on any failure, including a
    /// proxy that hasn't been redeployed with the /timeline route yet.
    /// </summary>
    Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default);
}

public sealed class RiotMatchClient : IRiotMatchClient
{
    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly ILogger<RiotMatchClient> _logger;

    public RiotMatchClient(HttpClient http, IConfigService config, ILogger<RiotMatchClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    // v3.2: 429-awareness. The Riot key's SUSTAINED budget (~100 calls / 2 min)
    // is far below the worker's burst allowance, so any long backfill WILL
    // exhaust a window and start drawing 429s — previously each one burned a
    // game as "failed" and whole runs churned uselessly (observed live: 475-game
    // laning walk with the majority 429ing). Honor Retry-After (or a
    // conservative default) and retry the SAME request: sleeping to the window
    // reset is the optimal pacing and self-adapts to whatever budget the proxy
    // actually enforces. Bursts between sleeps stay throttled by the callers.
    private const int MaxRateLimitRetries = 3;
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(3);

    public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default) =>
        GetJsonAsync(
            $"{RiotProxyEndpoint.BaseUrl}/match/{Uri.EscapeDataString(matchId)}?region={Uri.EscapeDataString(region)}",
            "Match", matchId, ct);

    public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default) =>
        GetJsonAsync(
            $"{RiotProxyEndpoint.BaseUrl}/timeline/{Uri.EscapeDataString(matchId)}?region={Uri.EscapeDataString(region)}",
            "Timeline", matchId, ct);

    private async Task<JsonElement?> GetJsonAsync(string url, string what, string matchId, CancellationToken ct)
    {
        var token = _config.RiotSessionToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("RiotMatchClient: no session token; user must reauth.");
            return null;
        }

        for (var attempt = 0; ; attempt++)
        {
            // A fresh HttpRequestMessage per attempt — they are single-use.
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            try
            {
                var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

                if ((int)res.StatusCode == 429 && attempt < MaxRateLimitRetries)
                {
                    var wait = RetryAfterOf(res) ?? DefaultRetryAfter;
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                    if (wait > MaxRetryAfter) wait = MaxRetryAfter;
                    _logger.LogInformation(
                        "{What} {MatchId} rate-limited; waiting {Seconds:0}s then retrying (attempt {Attempt})",
                        what, matchId, wait.TotalSeconds, attempt + 1);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                if (!res.IsSuccessStatusCode)
                {
                    // 404 from Riot is expected for matches outside the rolling
                    // window / IDs the proxy can't validate / an un-redeployed
                    // proxy without the /timeline route — debug, not warn.
                    if ((int)res.StatusCode == 404)
                    {
                        _logger.LogDebug("{What} {MatchId} not found upstream", what, matchId);
                    }
                    else
                    {
                        _logger.LogWarning("{What} {MatchId} fetch failed: {Status}", what, matchId, res.StatusCode);
                    }
                    return null;
                }

                return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{What} {MatchId} fetch errored", what, matchId);
                return null;
            }
        }
    }

    // Retry-After as a delay: delta form preferred, HTTP-date form converted.
    private static TimeSpan? RetryAfterOf(HttpResponseMessage res)
    {
        var ra = res.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return delta;
        if (ra.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }
}

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

    public async Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
    {
        var token = _config.RiotSessionToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("RiotMatchClient: no session token; user must reauth.");
            return null;
        }

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"{RiotProxyEndpoint.BaseUrl}/match/{Uri.EscapeDataString(matchId)}?region={Uri.EscapeDataString(region)}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        try
        {
            var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                // 404 from Riot is expected for matches outside the rolling window
                // or for IDs the proxy can't validate — log at debug, not warn.
                if ((int)res.StatusCode == 404)
                {
                    _logger.LogDebug("Match {MatchId} not found upstream", matchId);
                }
                else
                {
                    _logger.LogWarning("Match {MatchId} fetch failed: {Status}", matchId, res.StatusCode);
                }
                return null;
            }
            var doc = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            return doc;
        }
        catch (TaskCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Match {MatchId} fetch errored", matchId);
            return null;
        }
    }
}

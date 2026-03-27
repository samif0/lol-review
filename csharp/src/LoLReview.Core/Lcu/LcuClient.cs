#nullable enable

using System.Net.Http.Headers;
using System.Text.Json;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Lcu;

/// <summary>
/// HTTP client for the League Client Update (LCU) API.
/// Ported from Python client.py.
/// </summary>
public sealed class LcuClient : ILcuClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LcuClient> _logger;

    /// <summary>
    /// Creates an LcuClient using a pre-configured HttpClient.
    /// The HttpClient should be configured to bypass SSL certificate validation
    /// (the LCU uses self-signed certs) via IHttpClientFactory with a named client.
    /// </summary>
    public LcuClient(HttpClient httpClient, ILogger<LcuClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc />
    public void Configure(LcuCredentials credentials)
    {
        _httpClient.BaseAddress = new Uri(credentials.BaseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials.AuthHeaderValue);
    }

    /// <inheritdoc />
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        try
        {
            await GetAsync("/lol-summoner/v1/current-summoner", ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetCurrentSummonerAsync(CancellationToken ct = default)
    {
        return await GetAsync("/lol-summoner/v1/current-summoner", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GamePhase> GetGameflowPhaseAsync(CancellationToken ct = default)
    {
        try
        {
            var element = await GetAsync("/lol-gameflow/v1/gameflow-phase", ct).ConfigureAwait(false);
            var phaseString = element?.GetString();
            return GamePhaseExtensions.ParsePhase(phaseString);
        }
        catch
        {
            return GamePhase.None;
        }
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetEndOfGameStatsAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetAsync("/lol-end-of-game/v1/eog-stats-block", ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> GetLobbyQueueIdAsync(CancellationToken ct = default)
    {
        try
        {
            var session = await GetAsync("/lol-gameflow/v1/session", ct).ConfigureAwait(false);
            if (session is JsonElement el
                && el.TryGetProperty("gameData", out var gameData)
                && gameData.TryGetProperty("queue", out var queue)
                && queue.TryGetProperty("id", out var id)
                && id.TryGetInt32(out var queueId))
            {
                return queueId;
            }
        }
        catch
        {
            // Ignored — return -1 on failure
        }

        return -1;
    }

    /// <inheritdoc />
    public async Task<List<JsonElement>> GetMatchHistoryAsync(
        int begin = 0, int count = 5, CancellationToken ct = default)
    {
        try
        {
            var summoner = await GetCurrentSummonerAsync(ct).ConfigureAwait(false);
            if (summoner is not JsonElement summonerEl
                || !summonerEl.TryGetProperty("puuid", out var puuidProp))
            {
                return [];
            }

            var puuid = puuidProp.GetString();
            if (string.IsNullOrEmpty(puuid))
                return [];

            var data = await GetAsync(
                $"/lol-match-history/v1/products/lol/{puuid}/matches?begIndex={begin}&endIndex={begin + count}",
                ct).ConfigureAwait(false);

            if (data is JsonElement dataEl)
            {
                // Response may be { "games": [...] } or a raw array
                if (dataEl.ValueKind == JsonValueKind.Object
                    && dataEl.TryGetProperty("games", out var games)
                    && games.ValueKind == JsonValueKind.Array)
                {
                    return [.. games.EnumerateArray()];
                }

                if (dataEl.ValueKind == JsonValueKind.Array)
                {
                    return [.. dataEl.EnumerateArray()];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch match history");
        }

        return [];
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetRankedStatsAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetAsync("/lol-ranked/v1/current-ranked-stats", ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // ── Internal helper ─────────────────────────────────────────────────

    private async Task<JsonElement?> GetAsync(string endpoint, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(endpoint, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }
}

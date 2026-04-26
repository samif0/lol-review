#nullable enable

using System.Net.Http.Headers;
using System.Text.Json;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Lcu;

/// <summary>
/// HTTP client for the League Client Update (LCU) API.
/// Ported from Python client.py.
/// </summary>
public sealed class LcuClient : ILcuClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LcuClient> _logger;
    private Dictionary<int, string>? _championNamesById;
    private string? _baseUrl;
    private string? _authHeaderValue;

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
        _baseUrl = credentials.BaseUrl;
        _authHeaderValue = credentials.AuthHeaderValue;
    }

    /// <inheritdoc />
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        try
        {
            await GetAsync("/lol-summoner/v1/current-summoner", ct).ConfigureAwait(false);
            CoreDiagnostics.WriteVerbose("LCU: LcuClient IsConnectedAsync success");
            return true;
        }
        catch (Exception ex)
        {
            CoreDiagnostics.WriteVerbose($"LCU: LcuClient IsConnectedAsync exception={ex.GetType().Name}:{ex.Message}");
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
        // Do NOT catch exceptions here — let them propagate so GameMonitorService
        // can detect disconnection and reset state via HandleDisconnectedAsync.
        var element = await GetAsync("/lol-gameflow/v1/gameflow-phase", ct).ConfigureAwait(false);
        var phaseString = element?.GetString();
        return GamePhaseExtensions.ParsePhase(phaseString);
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
                CoreDiagnostics.WriteVerbose("LCU: GetMatchHistoryAsync missing current summoner puuid");
                return [];
            }

            var puuid = puuidProp.GetString();
            if (string.IsNullOrEmpty(puuid))
            {
                CoreDiagnostics.WriteVerbose("LCU: GetMatchHistoryAsync empty current summoner puuid");
                return [];
            }

            CoreDiagnostics.WriteVerbose($"LCU: GetMatchHistoryAsync requesting begin={begin} count={count}");

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
                    CoreDiagnostics.WriteVerbose($"LCU: GetMatchHistoryAsync parsed flat games array count={games.GetArrayLength()}");
                    return [.. games.EnumerateArray()];
                }

                // Riot currently nests match rows under { "games": { "games": [...] } }
                if (dataEl.ValueKind == JsonValueKind.Object
                    && dataEl.TryGetProperty("games", out var gamesWrapper)
                    && gamesWrapper.ValueKind == JsonValueKind.Object
                    && gamesWrapper.TryGetProperty("games", out var nestedGames)
                    && nestedGames.ValueKind == JsonValueKind.Array)
                {
                    CoreDiagnostics.WriteVerbose($"LCU: GetMatchHistoryAsync parsed nested games array count={nestedGames.GetArrayLength()}");
                    return [.. nestedGames.EnumerateArray()];
                }

                if (dataEl.ValueKind == JsonValueKind.Array)
                {
                    CoreDiagnostics.WriteVerbose($"LCU: GetMatchHistoryAsync parsed raw array count={dataEl.GetArrayLength()}");
                    return [.. dataEl.EnumerateArray()];
                }

                CoreDiagnostics.WriteVerbose($"LCU: GetMatchHistoryAsync unexpected response kind={dataEl.ValueKind}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch match history");
            CoreDiagnostics.WriteVerbose($"LCU: GetMatchHistoryAsync exception={ex.GetType().Name}:{ex.Message}");
        }

        return [];
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetMatchDetailsAsync(long gameId, CancellationToken ct = default)
    {
        try
        {
            return await GetAsync($"/lol-match-history/v1/games/{gameId}", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch match details for game {GameId}", gameId);
            CoreDiagnostics.WriteVerbose($"LCU: GetMatchDetailsAsync exception gameId={gameId} type={ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetChampionNameAsync(int championId, CancellationToken ct = default)
    {
        if (championId <= 0)
            return null;

        if (_championNamesById is not null
            && _championNamesById.TryGetValue(championId, out var cachedName))
        {
            return cachedName;
        }

        try
        {
            var data = await GetAsync("/lol-game-data/assets/v1/champion-summary.json", ct).ConfigureAwait(false);
            if (data is not JsonElement dataEl || dataEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var championNames = new Dictionary<int, string>();
            foreach (var champion in dataEl.EnumerateArray())
            {
                var id = champion.GetPropertyIntOrDefault("id", 0);
                if (id <= 0)
                    continue;

                var name = champion.GetPropertyOrDefault("alias", "");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = champion.GetPropertyOrDefault("name", "");
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    championNames[id] = name;
                }
            }

            _championNamesById = championNames;

            return championNames.TryGetValue(championId, out var resolvedName)
                ? resolvedName
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve champion name for championId {ChampionId}", championId);
            return null;
        }
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

    /// <inheritdoc />
    public async Task<(string MyChampion, string EnemyLaner, string MyPosition)> GetChampSelectInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var session = await GetAsync("/lol-champ-select/v1/session", ct).ConfigureAwait(false);
            if (session is not JsonElement el || el.ValueKind != JsonValueKind.Object)
                return ("", "", "");

            // Find local player's cell ID
            var localCellId = -1;
            if (el.TryGetProperty("localPlayerCellId", out var cellIdProp))
                localCellId = cellIdProp.GetInt32();

            var myChampion = "";
            var myPosition = "";
            var enemyLaner = "";
            var myTeamIndex = -1;

            // myTeam: find local player's champion + position + index
            if (el.TryGetProperty("myTeam", out var myTeam) && myTeam.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var member in myTeam.EnumerateArray())
                {
                    var cellId = member.GetPropertyIntOrDefault("cellId", -99);
                    if (cellId == localCellId)
                    {
                        var champId = member.GetPropertyIntOrDefault("championId", 0);
                        if (champId > 0)
                            myChampion = await GetChampionNameAsync(champId, ct).ConfigureAwait(false) ?? "";
                        myPosition = member.GetPropertyOrDefault("assignedPosition", "");
                        myTeamIndex = idx;
                        break;
                    }
                    idx++;
                }
            }

            // theirTeam: enemies don't expose their assignedPosition during
            // champ select (it's a Riot privacy choice), so position-matching
            // returns nothing. In Solo/Flex Queue both teams are ordered by
            // role (Top, Jg, Mid, Bot, Supp), so the same index in theirTeam
            // is the lane opponent. Fall back to position matching only for
            // queues where enemy positions ARE exposed (rare but cheap to try).
            if (el.TryGetProperty("theirTeam", out var theirTeam)
                && theirTeam.ValueKind == JsonValueKind.Array)
            {
                JsonElement? matched = null;

                // Pass 1: position match (handles odd queues that expose it).
                if (!string.IsNullOrEmpty(myPosition))
                {
                    foreach (var member in theirTeam.EnumerateArray())
                    {
                        var pos = member.GetPropertyOrDefault("assignedPosition", "");
                        if (pos.Equals(myPosition, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = member;
                            break;
                        }
                    }
                }

                // Pass 2: same index in theirTeam as the local player in myTeam.
                if (matched is null && myTeamIndex >= 0)
                {
                    int idx = 0;
                    foreach (var member in theirTeam.EnumerateArray())
                    {
                        if (idx == myTeamIndex) { matched = member; break; }
                        idx++;
                    }
                }

                if (matched is JsonElement m)
                {
                    var champId = m.GetPropertyIntOrDefault("championId", 0);
                    if (champId > 0)
                        enemyLaner = await GetChampionNameAsync(champId, ct).ConfigureAwait(false) ?? "";
                }
            }

            // Normalize position to Riot's uppercase form (LCU returns lowercase,
            // Riot's match-v5 returns uppercase). Downstream role comparisons assume uppercase.
            return (myChampion, enemyLaner, (myPosition ?? "").ToUpperInvariant());
        }
        catch
        {
            return ("", "", "");
        }
    }

    // ── Internal helper ─────────────────────────────────────────────────

    private async Task<JsonElement?> GetAsync(string endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_authHeaderValue))
        {
            throw new InvalidOperationException("LCU client has not been configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_baseUrl), endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authHeaderValue);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }
}

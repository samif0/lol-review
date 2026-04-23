#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Lcu;

/// <summary>
/// HTTP client for the League Live Client Data API (https://127.0.0.1:2999).
/// Ported from Python live_events.py module-level functions.
/// </summary>
public sealed class LiveEventApi : ILiveEventApi
{
    private const string BaseUrl = "https://127.0.0.1:2999";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly ILogger<LiveEventApi> _logger;

    /// <summary>
    /// Creates a LiveEventApi using a pre-configured HttpClient.
    /// The HttpClient should be configured to bypass SSL certificate validation.
    /// </summary>
    public LiveEventApi(HttpClient httpClient, ILogger<LiveEventApi> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = RequestTimeout;
    }

    /// <inheritdoc />
    public async Task<string?> GetActivePlayerNameAsync(CancellationToken ct = default)
    {
        var activePlayerName = await GetAsync("/liveclientdata/activeplayername", ct).ConfigureAwait(false);
        if (activePlayerName is JsonElement activePlayerNameEl
            && activePlayerNameEl.ValueKind == JsonValueKind.String)
        {
            var name = activePlayerNameEl.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        var data = await GetAsync("/liveclientdata/activeplayer", ct).ConfigureAwait(false);
        if (data is not JsonElement el)
            return null;

        return ResolveActivePlayerName(el);
    }

    internal static string? ResolveActivePlayerName(JsonElement el)
    {
        if (el.TryGetProperty("riotIdGameName", out var riotIdGameName)
            && riotIdGameName.ValueKind == JsonValueKind.String)
        {
            var name = riotIdGameName.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        if (el.TryGetProperty("summonerName", out var summonerName)
            && summonerName.ValueKind == JsonValueKind.String)
        {
            var name = summonerName.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        if (el.TryGetProperty("riotId", out var riotId) && riotId.ValueKind == JsonValueKind.String)
        {
            var name = riotId.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return await GetAsync("/liveclientdata/activeplayer", ct).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async Task<List<JsonElement>?> FetchEventsAsync(CancellationToken ct = default)
    {
        var data = await GetAsync("/liveclientdata/eventdata", ct).ConfigureAwait(false);
        if (data is not JsonElement el)
            return null;

        // Response is typically { "Events": [...] }
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("Events", out var events)
            && events.ValueKind == JsonValueKind.Array)
        {
            return [.. events.EnumerateArray()];
        }

        return null;
    }

    // ── Internal helper ─────────────────────────────────────────────────

    private async Task<JsonElement?> GetAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}

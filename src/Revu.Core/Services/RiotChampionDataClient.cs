#nullable enable

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>v2.16.1: minimal subset of a champion's ability data for the
/// pre-game intel rotator. Cooldowns are arrays-by-rank (5 entries for QWE,
/// 3 for R). All four slots are always populated; an empty <c>Name</c> means
/// the upstream payload was malformed and the consumer should fall back.</summary>
public sealed record ChampionAbility(
    string Slot,        // "Q" / "W" / "E" / "R" / "P" (passive)
    string Name,
    IReadOnlyList<double> CooldownByRank);

public sealed record ChampionAbilities(
    int ChampionId,
    string Name,
    string Alias,
    IReadOnlyList<ChampionAbility> Abilities);

/// <summary>v2.16.1: read-only static-data client for champion ability info.
/// Pulls from CommunityDragon's public mirror — no auth, no rate limits in
/// practice, and the upstream is just S3-backed JSON. Used for the pre-game
/// intel rotator on PreGamePage.
/// </summary>
public interface IRiotChampionDataClient
{
    /// <summary>Resolve an LCU display name (e.g. "Kai'Sa", "Kaisa", "Wukong",
    /// "Renata Glasc") to its numeric champion id. Returns 0 when unresolved.
    /// </summary>
    Task<int> ResolveChampionIdAsync(string displayNameOrAlias, CancellationToken ct = default);

    /// <summary>Fetch a champion's ability data (Q/W/E/R cooldowns + names).
    /// Cached on disk after the first successful fetch.</summary>
    Task<ChampionAbilities?> GetChampionAbilitiesAsync(int championId, CancellationToken ct = default);
}

public sealed class RiotChampionDataClient : IRiotChampionDataClient
{
    private const string SummaryUrl =
        "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-summary.json";

    private static string ChampionUrl(int id) =>
        $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champions/{id}.json";

    private readonly HttpClient _http;
    private readonly ILogger<RiotChampionDataClient> _logger;
    private readonly string _cacheDir;
    private Dictionary<string, int>? _summaryCache;
    private readonly SemaphoreSlim _summaryGate = new(1, 1);

    public RiotChampionDataClient(HttpClient http, ILogger<RiotChampionDataClient> logger)
    {
        _http = http;
        _logger = logger;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Revu",
            "champion_data");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<int> ResolveChampionIdAsync(string displayNameOrAlias, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayNameOrAlias)) return 0;
        var summary = await EnsureSummaryAsync(ct).ConfigureAwait(false);
        if (summary is null) return 0;

        var key = NormalizeKey(displayNameOrAlias);
        return summary.TryGetValue(key, out var id) ? id : 0;
    }

    public async Task<ChampionAbilities?> GetChampionAbilitiesAsync(int championId, CancellationToken ct = default)
    {
        if (championId <= 0) return null;

        // Disk cache — these payloads are large (~100kB) and the data only
        // changes at patch boundaries, so we cache forever and let users
        // delete the dir if a new champion is missing.
        var cachePath = Path.Combine(_cacheDir, $"{championId}.json");
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
                var doc = JsonSerializer.Deserialize<JsonElement>(cached);
                return ParseChampion(doc, championId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Champion cache read failed for {Id}", championId);
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ChampionUrl(championId));
            var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("CDragon champion {Id} fetch failed: {Status}", championId, res.StatusCode);
                return null;
            }
            var raw = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = JsonSerializer.Deserialize<JsonElement>(raw);
            try { await File.WriteAllTextAsync(cachePath, raw, ct).ConfigureAwait(false); } catch { }
            return ParseChampion(doc, championId);
        }
        catch (TaskCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Champion {Id} fetch errored", championId);
            return null;
        }
    }

    private async Task<Dictionary<string, int>?> EnsureSummaryAsync(CancellationToken ct)
    {
        if (_summaryCache is not null) return _summaryCache;
        await _summaryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_summaryCache is not null) return _summaryCache;

            var cachePath = Path.Combine(_cacheDir, "champion-summary.json");
            string? raw = null;
            try
            {
                if (File.Exists(cachePath))
                    raw = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
            }
            catch { }

            if (raw is null)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, SummaryUrl);
                var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogDebug("CDragon summary fetch failed: {Status}", res.StatusCode);
                    return null;
                }
                raw = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                try { await File.WriteAllTextAsync(cachePath, raw, ct).ConfigureAwait(false); } catch { }
            }

            var doc = JsonSerializer.Deserialize<JsonElement>(raw);
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (doc.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in doc.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                    var id = idEl.GetInt32();
                    if (id <= 0) continue; // -1 is the "None" placeholder

                    if (entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        map[NormalizeKey(n.GetString() ?? "")] = id;
                    if (entry.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String)
                        map[NormalizeKey(a.GetString() ?? "")] = id;
                }
            }
            _summaryCache = map;
            return map;
        }
        finally
        {
            _summaryGate.Release();
        }
    }

    /// <summary>Strip apostrophes/spaces/punct + lowercase so "Kai'Sa",
    /// "kaisa", and "Kai Sa" all collapse to the same lookup key.</summary>
    private static string NormalizeKey(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static ChampionAbilities? ParseChampion(JsonElement doc, int championId)
    {
        var name = doc.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? (n.GetString() ?? "") : "";
        var alias = doc.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String
            ? (a.GetString() ?? "") : "";

        var abilities = new List<ChampionAbility>();

        // Passive
        if (doc.TryGetProperty("passive", out var passive))
            abilities.Add(ParseAbility(passive, "P"));

        // Spells (array of 4: Q, W, E, R)
        if (doc.TryGetProperty("spells", out var spells) && spells.ValueKind == JsonValueKind.Array)
        {
            string[] slots = ["Q", "W", "E", "R"];
            int i = 0;
            foreach (var spell in spells.EnumerateArray())
            {
                if (i >= slots.Length) break;
                abilities.Add(ParseAbility(spell, slots[i]));
                i++;
            }
        }

        if (abilities.Count == 0) return null;
        return new ChampionAbilities(championId, name, alias, abilities);
    }

    private static ChampionAbility ParseAbility(JsonElement el, string slot)
    {
        var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? (n.GetString() ?? "") : "";
        var cds = new List<double>();
        if (el.TryGetProperty("cooldown", out var cd) && cd.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in cd.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.Number) cds.Add(v.GetDouble());
            }
        }
        else if (el.TryGetProperty("cooldownCoefficients", out var cc) && cc.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in cc.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.Number) cds.Add(v.GetDouble());
            }
        }
        return new ChampionAbility(slot, name, cds);
    }
}

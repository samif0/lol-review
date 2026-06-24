#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Stamps DEATH events that were jungle ganks. A "jungle gank" (the user's
/// definition) is a death in laning phase where the enemy JUNGLER was on the kill —
/// the killer OR an assister. The Live Client kill-feed gives summoner/Riot names for
/// the killer and assisters, so the caller resolves the enemy jungler's summoner
/// name(s) from end-of-game data (role → champion → summoner) and hands them in here.
///
/// <para>This is a derived ATTRIBUTE on the existing DEATH row (Details.jungle_gank =
/// true, killed_by_role = "jungle"), NOT a new event type — so the death audit and the
/// timeline keep one marker per death and don't double-count. The matching JUNGLE_GANK
/// trackable token is derived from this attribute at read time
/// (<see cref="ObjectiveEventTieResolver"/>), exactly like the per-spell / trade tokens.</para>
///
/// Pure and DB-free: stamping rewrites each DEATH event's Details JSON in place.
/// </summary>
public static class JungleGankClassifier
{
    /// <summary>Laning phase cutoff (seconds). A jungler kill/assist on you at or before
    /// 14:00 reads as a gank; later it's mid-game rotation, not a lane gank.</summary>
    public const int LaningPhaseEndSeconds = 14 * 60;

    /// <summary>
    /// Mutate <paramref name="events"/> in place: for every DEATH at or before
    /// <see cref="LaningPhaseEndSeconds"/> whose killer or an assister is one of
    /// <paramref name="enemyJunglerNames"/>, add <c>jungle_gank: true</c> +
    /// <c>killed_by_role: "jungle"</c> to its Details. Returns the number stamped.
    /// No-op (returns 0) when the jungler is unknown — better to flag nothing than to
    /// guess. Case-insensitive name match; the kill-feed and EOG agree on summoner names.
    /// </summary>
    public static int Stamp(IReadOnlyList<GameEvent> events, IReadOnlyCollection<string> enemyJunglerNames)
    {
        if (events is null || events.Count == 0) return 0;
        // Build the match set with each name AND its bare game-name half (pre-#tag), so a
        // "gameName#tag" on one side matches a bare "gameName" on the other. The kill-feed
        // uses the Riot-ID game name; the caller supplies every EOG name form it can. See
        // [[bug_puuid_lcu_vs_riot_scoping]] for why a naive summonerName compare misses.
        var junglers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in enemyJunglerNames ?? Array.Empty<string>())
            foreach (var form in NameForms(raw))
                junglers.Add(form);
        if (junglers.Count == 0) return 0; // jungler unknown → don't guess

        var stamped = 0;
        foreach (var e in events)
        {
            if (!string.Equals(e.EventType, GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.GameTimeS > LaningPhaseEndSeconds) continue; // out of laning phase

            var (killer, assisters) = ReadKillerAndAssisters(e);
            var onTheKill = NameForms(killer).Any(junglers.Contains)
                || assisters.Any(a => NameForms(a).Any(junglers.Contains));
            if (!onTheKill) continue;

            if (TryStampDetails(e)) stamped++;
        }
        return stamped;
    }

    // A name plus its bare game-name half ("Faker#NA1" → ["Faker#NA1", "Faker"]). Empty
    // forms are dropped. Lets a tagged Riot ID match a bare game name on either side.
    private static IEnumerable<string> NameForms(string? name)
    {
        var s = name?.Trim() ?? "";
        if (s.Length == 0) yield break;
        yield return s;
        var hash = s.IndexOf('#');
        if (hash > 0)
        {
            var bare = s[..hash].Trim();
            if (bare.Length > 0 && !string.Equals(bare, s, StringComparison.OrdinalIgnoreCase))
                yield return bare;
        }
    }

    // Pull the killer + assister names back out of a DEATH event's Details JSON.
    private static (string Killer, IReadOnlyList<string> Assisters) ReadKillerAndAssisters(GameEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}")
            return ("", Array.Empty<string>());
        try
        {
            using var doc = JsonDocument.Parse(e.Details);
            var root = doc.RootElement;
            var killer = root.TryGetProperty("killer", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() ?? ""
                : "";
            var assisters = new List<string>();
            if (root.TryGetProperty("assisters", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(s)) assisters.Add(s.Trim());
                }
            }
            return (killer.Trim(), assisters);
        }
        catch
        {
            return ("", Array.Empty<string>());
        }
    }

    // Add jungle_gank + killed_by_role to the event's Details, preserving existing keys.
    private static bool TryStampDetails(GameEvent e)
    {
        try
        {
            var node = string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}"
                ? new JsonObject()
                : JsonNode.Parse(e.Details) as JsonObject ?? new JsonObject();
            node["jungle_gank"] = true;
            node["killed_by_role"] = "jungle";
            e.Details = node.ToJsonString();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

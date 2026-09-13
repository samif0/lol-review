using System.Text.Json.Nodes;
using Revu.Core.Models;

namespace Revu.Core.Services.EventProcessing;

/// <summary>Read projection only: retain raw rows and correction identities for audit/review.</summary>
public static class EventEligibility
{
    private static readonly HashSet<string> Baseline = new(StringComparer.OrdinalIgnoreCase)
    { "KILL", "DEATH", "ASSIST", "DRAGON", "BARON", "HERALD", "TURRET", "INHIBITOR", "FIRST_BLOOD", "MULTI_KILL" };

    public static bool IsEligible(GameEvent item)
    {
        try
        {
            var details = JsonNode.Parse(item.Details) as JsonObject;
            if (details?["correction"] is JsonObject || details?["source"]?.GetValue<string>() == "reviewed_encounter") return true;
            if (details?["processing"] is JsonObject processing)
                return processing["version"]?.GetValue<int>() == 1
                    && processing["verification"]?.GetValue<string>() == "supported"
                    && processing["shadow"]?.GetValue<bool>() == false
                    && processing["evidence"] is JsonArray { Count: > 0 };
            return Baseline.Contains(item.EventType);
        }
        catch { return false; }
    }

    public static IReadOnlyList<GameEvent> ForConsumers(IEnumerable<GameEvent> events) =>
        events.Where(IsEligible).Select(Project).ToArray();

    public static bool WasSubscribed(GameEvent item, long objectiveId)
    {
        try
        {
            var node = JsonNode.Parse(item.Details);
            if (node?["correction"] is JsonObject || node?["source"]?.GetValue<string>() == "reviewed_encounter") return true;
            if (node?["processing"] is not JsonObject processing) return true;
            return processing["objectiveIds"] is JsonArray ids && ids.Any(id => id?.GetValue<long>() == objectiveId);
        }
        catch { return false; }
    }

    public static IReadOnlyList<string> StoredTokens(GameEvent item)
    {
        try
        {
            var node = JsonNode.Parse(item.Details);
            if (node?["correction"] is not null) return [];
            return node?["processing"]?["tokens"] is JsonArray tokens
                ? tokens.Select(t => t!.GetValue<string>()).ToArray() : [];
        }
        catch { return []; }
    }

    private static GameEvent Project(GameEvent item)
    {
        // A verified death does not verify heuristic attributes added by later analyzers.
        try
        {
            var details = JsonNode.Parse(item.Details) as JsonObject;
            if (details is null || details["correction"] is not null || details["source"]?.GetValue<string>() == "reviewed_encounter"
                || details["processing"] is not null) return item;
            foreach (var key in details.Select(p => p.Key).Where(k => k.StartsWith("fight_", StringComparison.Ordinal)
                || k.StartsWith("jungle_", StringComparison.Ordinal) || k.Contains("_jg_", StringComparison.Ordinal)
                || k is "fog_death" or "map_state" or "killed_by_role").ToArray()) details.Remove(key);
            return new GameEvent { Id = item.Id, GameId = item.GameId, EventType = item.EventType,
                GameTimeS = item.GameTimeS, EventKey = item.EventKey, Details = details.ToJsonString() };
        }
        catch { return item; }
    }
}

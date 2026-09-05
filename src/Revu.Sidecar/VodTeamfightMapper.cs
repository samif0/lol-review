#nullable enable

using System.Text.Json;
using Revu.Core.Constants;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// One <see cref="TeamfightSpan"/> (the shared fight definition every read path uses)
/// to ONE TEAMFIGHT timeline entry for the VOD snapshot: the loud pin for a fight the
/// player was in (short label = its numbers, hue = its verdict), the dim pin for a fight
/// without the player, and today's "TF" pin for a synthetic own-event cluster the
/// post-game pass has not replaced yet. Pure: the caller resolves the objective ties.
/// </summary>
public static class VodTeamfightMapper
{
    private const string UpHex = "#8ee7ba";
    private const string EvenHex = "#f3a3a8";
    private const string DownHex = "#f26d7d";
    private const string AwayHex = "#9fb0c3";

    public static VodEventDto Map(TeamfightSpan span, IReadOnlyList<ObjectiveTie> ties, long id)
    {
        var stored = span.Stored;
        var away = stored is not null && span.Self == TeamfightClustering.SelfAway;
        var details = stored is null ? Details.Empty : Details.Read(stored);

        var shortLabel = span.Numbers.Length > 0 ? span.Numbers : "TF";
        var summary = stored is null
            ? $"{FormatClock(span.StartS)}–{FormatClock(span.EndS)} · {span.Members.Count} events"
            : away ? AwaySummary(span, details) : OwnSummary(span, details);
        var colorHex = away ? AwayHex : span.Verdict switch
        {
            TeamfightClustering.VerdictUp => UpHex,
            TeamfightClustering.VerdictDown => DownHex,
            _ => EvenHex,
        };

        var first = ties.Count > 0 ? ties[0] : default;
        var objIds = ties.Count > 0 ? ties.Select(t => t.ObjectiveId).ToList() : null;

        return new VodEventDto(
            Id: id,
            EventType: GameEvent.TrackableTokens.TeamfightToken,
            GameTimeSeconds: span.StartS,
            TimeLabel: FormatClock(span.StartS),
            ShortLabel: away ? "TF" : shortLabel,
            Label: away ? "Fight without you" : "Teamfight",
            Summary: summary,
            Kind: away ? "teamfight-away" : "teamfight",
            ColorHex: colorHex,
            ObjectiveId: ties.Count > 0 ? first.ObjectiveId : null,
            ObjectiveTitle: ties.Count > 0 ? first.Title : "",
            ObjectiveColorHex: ties.Count > 0 ? first.Color : "",
            ObjectiveIds: objIds,
            Teamfight: new VodTeamfightDto(
                StartSeconds: span.FightStartS,
                EndSeconds: span.FightEndS,
                Numbers: span.Numbers,
                Verdict: span.Verdict,
                Self: stored is null ? TeamfightClustering.SelfIn : span.Self,
                Became: details.Became,
                EntrySeconds: details.EntryS,
                Kills: details.Kills,
                KillsFor: details.KillsFor,
                KillsAgainst: details.KillsAgainst,
                Outcome: details.Outcome,
                Allies: details.Allies,
                Enemies: details.Enemies,
                Stored: stored is not null));
    }

    // "3v2 when you committed · became 3v3 · kills 2 for, 1 against · Ahri (you), Lee Sin vs Zed"
    private static string OwnSummary(TeamfightSpan span, Details d)
    {
        var parts = new List<string>(4);
        parts.Add(span.Numbers.Length > 0 ? $"{span.Numbers} when you committed" : "Teamfight");
        if (d.Became.Length > 0 && d.Became != span.Numbers) parts.Add($"became {d.Became}");
        parts.Add($"kills {d.KillsFor} for, {d.KillsAgainst} against");
        var roster = Roster(d, markSelf: true);
        if (roster.Length > 0) parts.Add(roster);
        return string.Join(" · ", parts);
    }

    // "3v3 fight you were not in · kills 1 for, 2 against · Lee Sin, Jinx vs Zed, Nocturne"
    private static string AwaySummary(TeamfightSpan span, Details d)
    {
        var parts = new List<string>(3);
        parts.Add(span.Numbers.Length > 0 ? $"{span.Numbers} fight you were not in" : "Fight you were not in");
        parts.Add($"kills {d.KillsFor} for, {d.KillsAgainst} against");
        var roster = Roster(d, markSelf: false);
        if (roster.Length > 0) parts.Add(roster);
        return string.Join(" · ", parts);
    }

    // "Ahri (you), Lee Sin vs Zed": allies then enemies, "(you)" after the self champion.
    private static string Roster(Details d, bool markSelf)
    {
        var allies = d.Allies.Select(name =>
            markSelf && d.SelfChampion.Length > 0 && name == d.SelfChampion ? $"{name} (you)" : name);
        var left = string.Join(", ", allies);
        var right = string.Join(", ", d.Enemies);
        if (left.Length == 0 && right.Length == 0) return "";
        if (right.Length == 0) return left;
        if (left.Length == 0) return $"vs {right}";
        return $"{left} vs {right}";
    }

    // Mirrors VodSnapshotBuilder.FormatClock ("12:41").
    private static string FormatClock(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }

    /// <summary>The stored row's Details, read once; every field defaults on a
    /// malformed row so a bad payload still renders a pin.</summary>
    private sealed record Details(
        string Became,
        int? EntryS,
        int Kills,
        int KillsFor,
        int KillsAgainst,
        string Outcome,
        IReadOnlyList<string> Allies,
        IReadOnlyList<string> Enemies,
        string SelfChampion)
    {
        public static readonly Details Empty = new("", null, 0, 0, 0, "", [], [], "");

        public static Details Read(GameEvent row)
        {
            if (string.IsNullOrWhiteSpace(row.Details) || row.Details == "{}") return Empty;
            try
            {
                using var doc = JsonDocument.Parse(row.Details);
                var root = doc.RootElement;
                return new Details(
                    Became: Str(root, "became"),
                    EntryS: Int(root, "entry_s"),
                    Kills: Int(root, "kills") ?? 0,
                    KillsFor: Int(root, "kills_for") ?? 0,
                    KillsAgainst: Int(root, "kills_against") ?? 0,
                    Outcome: Str(root, "outcome"),
                    Allies: Names(root, "ally_champions"),
                    Enemies: Names(root, "enemy_champions"),
                    SelfChampion: SelfChampionOf(root));
            }
            catch { return Empty; }
        }

        private static string Str(JsonElement root, string property) =>
            root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static int? Int(JsonElement root, string property) =>
            root.TryGetProperty(property, out var v) && v.TryGetInt32(out var i) ? i : null;

        // Raw Match-V5 ids ("LeeSin") rendered as display names ("Lee Sin").
        private static IReadOnlyList<string> Names(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            return arr.EnumerateArray()
                .Where(static v => v.ValueKind == JsonValueKind.String)
                .Select(static v => GameConstants.CanonicalChampionName(v.GetString()))
                .Where(static n => n.Length > 0)
                .ToList();
        }

        // The player's own champion (participants[].self == true), as a display name.
        private static string SelfChampionOf(JsonElement root)
        {
            if (!root.TryGetProperty("participants", out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
            foreach (var p in arr.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                if (p.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True)
                    return GameConstants.CanonicalChampionName(Str(p, "champion"));
            }
            return "";
        }
    }
}

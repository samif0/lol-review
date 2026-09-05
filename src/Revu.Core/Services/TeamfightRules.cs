#nullable enable

using System.Text.Json;

namespace Revu.Core.Services;

/// <summary>
/// The game rules the teamfight analyzer leans on, kept pure and separately testable:
/// who is CREDITED on a Match-V5 kill, how long a death keeps a champion off the map,
/// and how a numbers comparison becomes a verdict. Nothing here reads positions — every
/// count is kill-feed credit, so a number can never claim the player "saw" anything.
/// </summary>
public static class TeamfightRules
{
    /// <summary>One timeline CHAMPION_KILL, reduced to what the numbers need.</summary>
    public sealed record Kill(
        long TMs,
        int KillerId,
        int VictimId,
        IReadOnlyList<int> AssistIds,
        IReadOnlyList<int> DamageReceivedFrom,
        IReadOnlyList<int> DamageDealtTo,
        bool HasPosition,
        double X,
        double Y)
    {
        /// <summary>Killed by a minion / turret / monster with no champion involved at
        /// all. Not a fight signal: it never clusters, tallies, or credits anyone.</summary>
        public bool IsLoneExecute => KillerId <= 0 && AssistIds.Count == 0
            && DamageReceivedFrom.Count == 0 && DamageDealtTo.Count == 0;
    }

    /// <summary>Base respawn by level on Summoner's Rift (seconds), levels 1..18.</summary>
    private static readonly double[] BaseRespawnSeconds =
        [10, 10, 12, 12, 14, 16, 20, 25, 28, 32.5, 35, 37.5, 40, 42.5, 45, 47.5, 50, 52.5];

    /// <summary>
    /// Respawn wait for a death at <paramref name="deathMs"/> at <paramref name="level"/>:
    /// base-by-level times (1 + time-increase factor). The factor steps every 15 s of game
    /// time: none before 15:00, +0.425 % per step until 30:00, +0.30 % per step until
    /// 45:00, +1.45 % per step until 55:00, frozen after. An approximation of the live
    /// rules that only decides whether an earlier victim is still dead at an instant.
    /// </summary>
    public static double RespawnSeconds(int level, long deathMs)
    {
        var clamped = Math.Clamp(level, 1, 18);
        var baseS = BaseRespawnSeconds[clamped - 1];
        var t = Math.Min(deathMs / 1000.0, 55 * 60);
        double tif = 0;
        if (t >= 15 * 60)
        {
            var band1 = Math.Min(t, 30 * 60) - 15 * 60;
            tif += Math.Floor(band1 / 15) * 0.00425;
        }
        if (t >= 30 * 60)
        {
            var band2 = Math.Min(t, 45 * 60) - 30 * 60;
            tif += Math.Floor(band2 / 15) * 0.0030;
        }
        if (t >= 45 * 60)
        {
            var band3 = t - 45 * 60;
            tif += Math.Floor(band3 / 15) * 0.0145;
        }
        return baseS * (1 + tif);
    }

    /// <summary>Everyone the kill feed credits on a kill: the killer, the victim, the
    /// assisters, and every champion that exchanged damage with the victim in the
    /// pre-kill window (Match-V5 victimDamageReceived / victimDamageDealt). Only ids in
    /// <paramref name="roster"/> count; a lone execute credits nobody.</summary>
    public static HashSet<int> Credited(Kill kill, IReadOnlySet<int> roster)
    {
        var set = new HashSet<int>();
        if (kill.IsLoneExecute) return set;
        void Add(int pid) { if (pid > 0 && roster.Contains(pid)) set.Add(pid); }
        Add(kill.KillerId);
        Add(kill.VictimId);
        foreach (var a in kill.AssistIds) Add(a);
        foreach (var p in kill.DamageReceivedFrom) if (p != kill.VictimId) Add(p);
        foreach (var p in kill.DamageDealtTo) if (p != kill.VictimId) Add(p);
        return set;
    }

    public static bool IsCredited(Kill kill, int pid, IReadOnlySet<int> roster) =>
        Credited(kill, roster).Contains(pid);

    public static string Verdict(int allies, int enemies) =>
        allies > enemies ? TeamfightClustering.VerdictUp
        : allies < enemies ? TeamfightClustering.VerdictDown
        : TeamfightClustering.VerdictEven;

    public static int Favor(int allies, int enemies) => Math.Sign(allies - enemies);

    public static string NumbersLabel(int allies, int enemies) => $"{allies}v{enemies}";

    public static string Outcome(int killsFor, int killsAgainst) =>
        killsFor > killsAgainst ? "won" : killsFor < killsAgainst ? "lost" : "even";

    /// <summary>Parse one timeline CHAMPION_KILL event; null when it is not one.</summary>
    public static Kill? ParseKill(JsonElement ev)
    {
        if (TimelineJson.Str(ev, "type") != "CHAMPION_KILL") return null;
        double x = 0, y = 0;
        var hasPos = ev.TryGetProperty("position", out var posEl)
            && TimelineTracks.TryReadPosition(posEl, out x, out y);
        return new Kill(
            TMs: TimelineJson.Long(ev, "timestamp"),
            KillerId: TimelineJson.Int(ev, "killerId"),
            VictimId: TimelineJson.Int(ev, "victimId"),
            AssistIds: TimelineJson.ReadIntArray(ev, "assistingParticipantIds"),
            DamageReceivedFrom: ReadParticipantIds(ev, "victimDamageReceived"),
            DamageDealtTo: ReadParticipantIds(ev, "victimDamageDealt"),
            HasPosition: hasPos,
            X: x,
            Y: y);
    }

    // The champion participant ids inside a damage array (minions, monsters and
    // turrets carry participantId 0 and are skipped). Missing array → empty.
    private static IReadOnlyList<int> ReadParticipantIds(JsonElement ev, string property)
    {
        if (!ev.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var ids = new List<int>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var pid = TimelineJson.Int(item, "participantId");
            if (pid > 0 && !ids.Contains(pid)) ids.Add(pid);
        }
        return ids;
    }
}

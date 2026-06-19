#nullable enable

using Revu.Core.Constants;

namespace Revu.Core.Models;

/// <summary>
/// Structured post-game statistics extracted from LCU data.
/// Ported from Python GameStats dataclass.
/// </summary>
public class GameStats
{
    // ── Identifiers ──────────────────────────────────────────────────

    public long GameId { get; set; }
    public long Timestamp { get; set; }
    public int GameDuration { get; set; }
    public string GameMode { get; set; } = "";
    public string GameType { get; set; } = "";
    public string QueueType { get; set; } = "";
    public int MapId { get; set; }

    // ── Player info ──────────────────────────────────────────────────

    public string SummonerName { get; set; } = "";

    /// <summary>
    /// v3.1.2 (schema v9): the stable Riot account id (PUUID) of the player this
    /// row belongs to, stamped at capture for account-scoped analytics (P3a).
    /// Unlike <see cref="SummonerName"/> it survives a Riot rename, so it can
    /// distinguish accounts without splitting one renamed account's history.
    /// Empty when the capture path couldn't resolve a PUUID (and on all legacy
    /// rows captured before v9); the lenient scope treats '' as the user's own.
    /// </summary>
    public string Puuid { get; set; } = "";
    public string ChampionName { get; set; } = "";
    public int ChampionId { get; set; }
    /// <summary>100 = blue, 200 = red.</summary>
    public int TeamId { get; set; }
    public string Position { get; set; } = "";
    public string Role { get; set; } = "";
    public string EnemyLaner { get; set; } = "";

    /// <summary>
    /// v2.16: JSON map of role→champion for both teams keyed from the user's
    /// perspective. See <c>EnemyLanerBackfillService.ExtractParticipantMap</c>
    /// for the shape. Empty until the backfill pass populates it.
    /// </summary>
    public string ParticipantMap { get; set; } = "";

    // ── Outcome ──────────────────────────────────────────────────────

    public bool Win { get; set; }

    // ── KDA ──────────────────────────────────────────────────────────

    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public double KdaRatio { get; set; }
    public int LargestKillingSpree { get; set; }
    public int LargestMultiKill { get; set; }
    public int DoubleKills { get; set; }
    public int TripleKills { get; set; }
    public int QuadraKills { get; set; }
    public int PentaKills { get; set; }
    public bool FirstBlood { get; set; }

    // ── Damage ───────────────────────────────────────────────────────

    public int TotalDamageDealt { get; set; }
    public int TotalDamageToChampions { get; set; }
    public int PhysicalDamageToChampions { get; set; }
    public int MagicDamageToChampions { get; set; }
    public int TrueDamageToChampions { get; set; }
    public int TotalDamageTaken { get; set; }
    public int DamageSelfMitigated { get; set; }
    public int LargestCriticalStrike { get; set; }

    // ── Economy ──────────────────────────────────────────────────────

    public int GoldEarned { get; set; }
    public int GoldSpent { get; set; }
    public int TotalMinionsKilled { get; set; }
    public int NeutralMinionsKilled { get; set; }
    public int CsTotal { get; set; }
    public double CsPerMin { get; set; }

    // ── Vision ───────────────────────────────────────────────────────

    public int VisionScore { get; set; }
    public int WardsPlaced { get; set; }
    public int WardsKilled { get; set; }
    public int ControlWardsPurchased { get; set; }

    // ── Objectives ───────────────────────────────────────────────────

    public int TurretKills { get; set; }
    public int InhibitorKills { get; set; }
    public int DragonKills { get; set; }
    public int BaronKills { get; set; }
    public int RiftHeraldKills { get; set; }

    // ── Healing & Utility ────────────────────────────────────────────

    public int TotalHeal { get; set; }
    public int TotalHealsOnTeammates { get; set; }
    public int TotalDamageShieldedOnTeammates { get; set; }
    public int TotalTimeCcDealt { get; set; }
    public int TimeCcingOthers { get; set; }

    // ── Spells & Items ───────────────────────────────────────────────

    public int Spell1Casts { get; set; }
    public int Spell2Casts { get; set; }
    public int Spell3Casts { get; set; }
    public int Spell4Casts { get; set; }
    public int Summoner1Id { get; set; }
    public int Summoner2Id { get; set; }
    public List<int> Items { get; set; } = [];

    // ── Level & XP ───────────────────────────────────────────────────

    public int ChampLevel { get; set; }

    // ── Team totals (for context) ────────────────────────────────────

    public int TeamKills { get; set; }
    public int TeamDeaths { get; set; }
    public double KillParticipation { get; set; }

    // ── Laning numbers (Match-V5 timeline backfill; schema v5) ───────
    // NULL until the timeline backfill has run for this game.

    public double? CsAt10 { get; set; }
    public int? GoldDiffAt10 { get; set; }
    public double? CsDiffAt10 { get; set; }

    // ── Review fields (populated from DB when present) ───────────────

    public string ReviewNotes { get; set; } = "";
    public int Rating { get; set; }
    public string Tags { get; set; } = "[]";
    public string Mistakes { get; set; } = "";
    public string WentWell { get; set; } = "";
    public string FocusNext { get; set; } = "";
    public string SpottedProblems { get; set; } = "";
    public string OutsideControl { get; set; } = "";
    public string WithinControl { get; set; } = "";
    public string Attribution { get; set; } = "";
    public string PersonalContribution { get; set; } = "";

    // ── Visibility ───────────────────────────────────────────────────

    /// <summary>When true the game is soft-deleted and hidden from all views.</summary>
    public bool IsHidden { get; set; }

    // ── Raw JSON for anything we might have missed ───────────────────

    public Dictionary<string, object> RawStats { get; set; } = [];

    // ── Live events collected during the game ────────────────────────

    public List<GameEvent> LiveEvents { get; set; } = [];

    // ── Computed display helpers ─────────────────────────────────────

    /// <summary>Human-readable date string from the Timestamp.</summary>
    public string DatePlayed =>
        Timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime.ToString("MMM d, yyyy h:mm tt")
            : "";

    /// <summary>Formatted game duration as "Xm Ys".</summary>
    public string DurationFormatted =>
        GameDuration > 0
            ? $"{GameDuration / 60}m {GameDuration % 60}s"
            : "";

    /// <summary>Best user-facing mode label, preferring queue labels over raw Riot modes.</summary>
    public string DisplayGameMode =>
        GameConstants.GetDisplayGameMode(GameMode, QueueType);
}

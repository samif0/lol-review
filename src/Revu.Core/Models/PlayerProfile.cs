#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// Comprehensive player profile aggregated from all data.
/// Powers the local suggestion engine and serves as feature vector
/// for a future AI coaching model. JSON-serializable, rank-agnostic.
/// </summary>
public class PlayerProfile
{
    /// <summary>All-time aggregated stats.</summary>
    public OverallStats Overall { get; set; } = new();

    /// <summary>Recent stats (last 20 games) — for suggestion thresholds.</summary>
    public OverallStats Recent { get; set; } = new();

    /// <summary>Per-champion performance breakdown.</summary>
    public List<ChampionStats> Champions { get; set; } = [];

    /// <summary>Matchup-specific stats.</summary>
    public List<MatchupStats> Matchups { get; set; } = [];

    /// <summary>Mental state / winrate correlation data.</summary>
    public MentalCorrelation Mental { get; set; } = new();

    /// <summary>Concept tag frequency analysis.</summary>
    public List<TagFrequency> ConceptTags { get; set; } = [];

    /// <summary>Objectives summary.</summary>
    public ObjectivesSummary Objectives { get; set; } = new();

    /// <summary>Recent form (last 10/20 game winrates, streaks).</summary>
    public RecentFormStats RecentForm { get; set; } = new();

    /// <summary>Grouped recurring spotted problems.</summary>
    public List<SpottedProblem> SpottedProblems { get; set; } = [];

    /// <summary>Per-role performance stats.</summary>
    public List<RoleStats> Roles { get; set; } = [];

    /// <summary>Performance bucketed by game duration.</summary>
    public List<DurationBucket> DurationBuckets { get; set; } = [];

    /// <summary>Session-level patterns.</summary>
    public SessionPatternStats SessionPatterns { get; set; } = new();
}

// ── Inner record types ────────────────────────────────────────────────

/// <summary>Aggregated stats (used for both overall and recent windows).</summary>
public record OverallStats
{
    public int TotalGames { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public double Winrate { get; init; }
    public double AvgKills { get; init; }
    public double AvgDeaths { get; init; }
    public double AvgAssists { get; init; }
    public double AvgKda { get; init; }
    public double AvgCsMin { get; init; }
    public double AvgVision { get; init; }
    public double AvgDamage { get; init; }
    public double AvgGold { get; init; }
    public double AvgGameDuration { get; init; }
}

/// <summary>Per-champion performance data.</summary>
public record ChampionStats
{
    public string ChampionName { get; init; } = "";
    public int Games { get; init; }
    public int Wins { get; init; }
    public double Winrate { get; init; }
    public double AvgKda { get; init; }
    public double AvgCsMin { get; init; }
    public double AvgDamage { get; init; }
}

/// <summary>Per-matchup performance data.</summary>
public record MatchupStats
{
    public string ChampionName { get; init; } = "";
    public string EnemyLaner { get; init; } = "";
    public int Games { get; init; }
    public int Wins { get; init; }
    public double Winrate { get; init; }
    public double AvgKda { get; init; }
}

/// <summary>Mental rating bracket vs. winrate correlation.</summary>
public record MentalCorrelation
{
    /// <summary>Winrate when mental rating is 1-3.</summary>
    public double LowWr { get; init; }

    /// <summary>Winrate when mental rating is 4-6.</summary>
    public double MidWr { get; init; }

    /// <summary>Winrate when mental rating is 7-10.</summary>
    public double HighWr { get; init; }

    /// <summary>Average mental rating across all rated games.</summary>
    public double AvgRating { get; init; } = 5;
}

/// <summary>Concept tag frequency entry.</summary>
public record TagFrequency
{
    public string Name { get; init; } = "";
    public string Polarity { get; init; } = "neutral";
    public int Count { get; init; }
    public double GamePct { get; init; }
}

/// <summary>Summary of objectives progress.</summary>
public record ObjectivesSummary
{
    public int ActiveCount { get; init; }
    public int CompletedCount { get; init; }
    public double AvgGamesToComplete { get; init; }
    public List<ActiveObjectiveInfo> Active { get; init; } = [];
}

/// <summary>Brief info about an active objective.</summary>
public record ActiveObjectiveInfo
{
    public string Title { get; init; } = "";
    public int Score { get; init; }
    public int GameCount { get; init; }
}

/// <summary>Recent form (last N game winrates and streak).</summary>
public record RecentFormStats
{
    public double Last10Wr { get; init; }
    public double Last20Wr { get; init; }
    public int WinStreak { get; init; }
}

/// <summary>A spotted problem aggregated from game reviews.</summary>
public record SpottedProblem
{
    public string Text { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>Per-role performance stats.</summary>
public record RoleStats
{
    public string Role { get; init; } = "";
    public int Games { get; init; }
    public int Wins { get; init; }
    public double Winrate { get; init; }
    public double AvgKda { get; init; }
}

/// <summary>Performance bucketed by game duration range.</summary>
public record DurationBucket
{
    public string Label { get; init; } = "";
    public int MinMinutes { get; init; }
    public int MaxMinutes { get; init; }
    public int Games { get; init; }
    public int Wins { get; init; }
    public double Winrate { get; init; }
}

/// <summary>Session-level patterns (games per session, time of day, etc.).</summary>
public record SessionPatternStats
{
    public double AvgGamesPerSession { get; init; }
    public double AvgSessionDurationMin { get; init; }
    public int TotalSessions { get; init; }
    public double AvgMentalRating { get; init; }
}

#nullable enable

using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Player profiling and smart objective suggestion generation.
/// Ported from Python analysis/profile.py and analysis/suggestions.py.
/// </summary>
public sealed class AnalysisService : IAnalysisService
{
    private readonly IGameRepository _games;
    private readonly ISessionLogRepository _sessionLog;
    private readonly IConceptTagRepository _conceptTags;
    private readonly IObjectivesRepository _objectives;
    private readonly ILogger<AnalysisService> _logger;

    public AnalysisService(
        IGameRepository games,
        ISessionLogRepository sessionLog,
        IConceptTagRepository conceptTags,
        IObjectivesRepository objectives,
        ILogger<AnalysisService> logger)
    {
        _games = games;
        _sessionLog = sessionLog;
        _conceptTags = conceptTags;
        _objectives = objectives;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Profile generation
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<PlayerProfile> GenerateProfileAsync()
    {
        var profile = new PlayerProfile();

        // Overall stats (all-time)
        try
        {
            var overall = await _games.GetOverallStatsAsync().ConfigureAwait(false);
            profile.Overall = new Models.OverallStats
            {
                TotalGames = overall.TotalGames,
                Wins = overall.TotalWins,
                Losses = overall.TotalGames - overall.TotalWins,
                Winrate = overall.Winrate,
                AvgKills = overall.AvgKills,
                AvgDeaths = overall.AvgDeaths,
                AvgAssists = overall.AvgAssists,
                AvgKda = overall.AvgKda,
                AvgCsMin = overall.AvgCsMin,
                AvgVision = overall.AvgVision,
            };
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: overall stats failed"); }

        // Recent stats (last 20 games)
        try
        {
            var recent = await _games.GetRecentStatsAsync(limit: 20).ConfigureAwait(false);
            profile.Recent = new Models.OverallStats
            {
                TotalGames = recent.Games,
                Winrate = recent.Winrate,
                AvgKills = recent.AvgKills,
                AvgDeaths = recent.AvgDeaths,
                AvgCsMin = recent.AvgCsMin,
                AvgVision = recent.AvgVision,
                AvgKda = recent.AvgKda,
            };
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: recent stats failed"); }

        // Per-champion performance
        try
        {
            var champStats = await _games.GetChampionStatsAsync().ConfigureAwait(false);
            profile.Champions = champStats.Select(c => new Models.ChampionStats
            {
                ChampionName = c.ChampionName,
                Games = c.GamesPlayed,
                Wins = c.Wins,
                Winrate = c.Winrate,
                AvgKda = c.AvgKda,
                AvgCsMin = c.AvgCsMin,
                AvgDamage = c.AvgDamage,
            }).ToList();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: champion stats failed"); }

        // Matchup stats
        try
        {
            var matchups = await _games.GetMatchupStatsAsync().ConfigureAwait(false);
            profile.Matchups = matchups.Select(m => new Models.MatchupStats
            {
                ChampionName = m.ChampionName,
                EnemyLaner = m.EnemyLaner,
                Games = m.Games,
                Wins = m.Wins,
                Winrate = m.Winrate,
                AvgKda = m.AvgKda,
            }).ToList();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: matchup stats failed"); }

        // Mental state data
        try
        {
            var correlation = await _sessionLog.GetMentalWinrateCorrelationAsync().ConfigureAwait(false);
            double lowWr = 0, midWr = 0, highWr = 0;
            foreach (var bracket in correlation)
            {
                var label = bracket.Bracket;
                if (label.Contains("1-3") || label == "Low") lowWr = bracket.Winrate;
                else if (label.Contains("4-6") || label == "Mid") midWr = bracket.Winrate;
                else if (label.Contains("7-10") || label == "High") highWr = bracket.Winrate;
            }

            double avgRating = 5;
            var trend = await _sessionLog.GetMentalTrendAsync(limit: 50).ConfigureAwait(false);
            if (trend.Count > 0)
            {
                avgRating = Math.Round(trend.Average(t => t.MentalRating), 1);
            }

            profile.Mental = new MentalCorrelation
            {
                LowWr = lowWr,
                MidWr = midWr,
                HighWr = highWr,
                AvgRating = avgRating,
            };
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: mental stats failed"); }

        // Concept tag frequency
        try
        {
            var tags = await _conceptTags.GetTagFrequencyAsync(limit: 20).ConfigureAwait(false);
            profile.ConceptTags = tags.Select(t => new Models.TagFrequency
            {
                Name = t.Name,
                Polarity = t.Polarity,
                Count = t.Count,
                GamePct = t.GamePercent,
            }).ToList();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: concept tags failed"); }

        // Objectives summary
        try
        {
            var allObjs = await _objectives.GetAllAsync().ConfigureAwait(false);
            var active = allObjs.Where(o => string.Equals(o.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();
            var completed = allObjs.Where(o => !string.Equals(o.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();

            double avgGames = 0;
            if (completed.Count > 0)
            {
                var counts = completed
                    .Select(o => o.GameCount)
                    .Where(c => c > 0)
                    .ToList();
                if (counts.Count > 0) avgGames = Math.Round(counts.Average(), 1);
            }

            profile.Objectives = new ObjectivesSummary
            {
                ActiveCount = active.Count,
                CompletedCount = completed.Count,
                AvgGamesToComplete = avgGames,
                Active = active.Select(o => new ActiveObjectiveInfo
                {
                    Title = o.Title,
                    Score = o.Score,
                    GameCount = o.GameCount,
                }).ToList(),
            };
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: objectives failed"); }

        // Recent form
        try
        {
            var recentCharts = await _games.GetRecentForChartsAsync(limit: 20).ConfigureAwait(false);
            if (recentCharts.Count > 0)
            {
                var last10 = recentCharts.TakeLast(10).ToList();
                var last20 = recentCharts.TakeLast(20).ToList();
                double l10Wr = last10.Count > 0
                    ? Math.Round(100.0 * last10.Count(g => g.Win) / last10.Count, 1) : 0;
                double l20Wr = last20.Count > 0
                    ? Math.Round(100.0 * last20.Count(g => g.Win) / last20.Count, 1) : 0;
                var winStreak = await _games.GetWinStreakAsync().ConfigureAwait(false);

                profile.RecentForm = new RecentFormStats
                {
                    Last10Wr = l10Wr,
                    Last20Wr = l20Wr,
                    WinStreak = winStreak,
                };
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: recent form failed"); }

        // Spotted problems (group similar ones)
        try
        {
            var problems = await _games.GetRecentSpottedProblemsAsync(limit: 50).ConfigureAwait(false);
            var counter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in problems)
            {
                var text = (p.SpottedProblems ?? "").Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    counter[text] = counter.GetValueOrDefault(text) + 1;
                }
            }
            profile.SpottedProblems = counter
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new Models.SpottedProblem { Text = kv.Key, Count = kv.Value })
                .ToList();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: spotted problems failed"); }

        // Role stats
        try
        {
            var roles = await _games.GetRoleStatsAsync().ConfigureAwait(false);
            profile.Roles = roles.Select(r => new Models.RoleStats
            {
                Role = r.Position,
                Games = r.Games,
                Wins = r.Wins,
                Winrate = r.Winrate,
                AvgKda = r.AvgKda,
            }).ToList();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: role stats failed"); }

        // Duration buckets
        try
        {
            var durations = await _games.GetDurationStatsAsync().ConfigureAwait(false);
            profile.DurationBuckets = durations.Select(d => new DurationBucket
            {
                Label = d.Bucket,
                Games = d.Games,
                Wins = d.Wins,
                Winrate = d.Winrate,
            }).ToList();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: duration stats failed"); }

        // Session patterns
        try
        {
            var patterns = await _sessionLog.GetSessionPatternsAsync().ConfigureAwait(false);
            profile.SessionPatterns = new SessionPatternStats
            {
                AvgGamesPerSession = patterns.AvgGamesPerSession,
                TotalSessions = patterns.TotalSessionDays,
                AvgMentalRating = patterns.AvgMentalDelta, // Use best available mapping
            };
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Profile: session patterns failed"); }

        return profile;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Suggestion engine — 7 deterministic rules
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public List<ObjectiveSuggestion> GenerateSuggestions(PlayerProfile profile, int limit = 3)
    {
        var suggestions = new List<ObjectiveSuggestion>();

        foreach (var rule in new Func<PlayerProfile, ObjectiveSuggestion?>[]
        {
            CheckVision,
            CheckCs,
            CheckDeaths,
            CheckMentalGap,
            CheckNegativeTags,
            CheckLosingMatchups,
            CheckSpottedProblems,
        })
        {
            try
            {
                var result = rule(profile);
                if (result is not null)
                    suggestions.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Suggestion rule {Rule} failed", rule.Method.Name);
            }
        }

        suggestions.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        return suggestions.Take(limit).ToList();
    }

    // ── Rule 1: Vision ──────────────────────────────────────────────

    private static ObjectiveSuggestion? CheckVision(PlayerProfile profile)
    {
        var avgVision = profile.Recent.AvgVision;
        if (avgVision <= 0 || avgVision >= 15) return null;

        return new ObjectiveSuggestion
        {
            Title = "Improve vision control",
            SkillArea = "Map awareness",
            Type = "primary",
            CompletionCriteria = "Average 15+ vision score per game",
            Description = $"Your recent vision score averages {avgVision:F0}. " +
                "Focus on placing wards at key timings (before objectives, " +
                "when pushing, and during roams) and buying control wards.",
            Reason = $"Your recent vision score ({avgVision:F0}) is below 15",
            Confidence = Math.Min(0.9, (15 - avgVision) / 15),
        };
    }

    // ── Rule 2: CS ──────────────────────────────────────────────────

    private static ObjectiveSuggestion? CheckCs(PlayerProfile profile)
    {
        var avgCs = profile.Recent.AvgCsMin;
        if (avgCs <= 0 || avgCs >= 6.0) return null;

        return new ObjectiveSuggestion
        {
            Title = "Improve CS per minute",
            SkillArea = "Laning",
            Type = "primary",
            CompletionCriteria = "Average 6+ CS per minute",
            Description = $"Your recent CS averages {avgCs:F1}/min. Practice last-hitting in " +
                "practice tool, focus on not missing uncontested minions, and " +
                "catch side waves between fights.",
            Reason = $"Your recent CS/min ({avgCs:F1}) is below 6.0",
            Confidence = Math.Min(0.9, (6.0 - avgCs) / 6.0),
        };
    }

    // ── Rule 3: Deaths ──────────────────────────────────────────────

    private static ObjectiveSuggestion? CheckDeaths(PlayerProfile profile)
    {
        var avgDeaths = profile.Recent.AvgDeaths;
        if (avgDeaths <= 0 || avgDeaths <= 6.0) return null;

        return new ObjectiveSuggestion
        {
            Title = "Reduce deaths per game",
            SkillArea = "Positioning & decision-making",
            Type = "primary",
            CompletionCriteria = "Average 6 or fewer deaths per game",
            Description = $"You're averaging {avgDeaths:F1} deaths recently. Before each fight, " +
                "ask: 'Can I die here? Is it worth?' Track your death reasons " +
                "to find the most common ones.",
            Reason = $"Your recent deaths ({avgDeaths:F1}/game) average above 6",
            Confidence = Math.Min(0.9, (avgDeaths - 6.0) / 6.0),
        };
    }

    // ── Rule 4: Mental gap ──────────────────────────────────────────

    private static ObjectiveSuggestion? CheckMentalGap(PlayerProfile profile)
    {
        var lowWr = profile.Mental.LowWr;
        var highWr = profile.Mental.HighWr;
        if (lowWr <= 0 || highWr <= 0) return null;

        var gap = highWr - lowWr;
        if (gap < 15) return null;

        return new ObjectiveSuggestion
        {
            Title = "Mental state management",
            SkillArea = "Mental",
            Type = "mental",
            CompletionCriteria = "Maintain mental rating 5+ in 80% of games",
            Description = $"Your winrate is {highWr:F0}% when mental is high (7-10) but {lowWr:F0}% when " +
                $"low (1-3) \u2014 a {gap:F0}pp gap. Practice recognizing tilt early, " +
                "take breaks after tough losses, and use the pre-game mood check.",
            Reason = $"Your winrate drops {gap:F0}pp when mental is low vs high",
            Confidence = Math.Min(0.95, gap / 40),
        };
    }

    // ── Rule 5: Negative tags ───────────────────────────────────────

    private static ObjectiveSuggestion? CheckNegativeTags(PlayerProfile profile)
    {
        foreach (var tag in profile.ConceptTags)
        {
            if (!string.Equals(tag.Polarity, "negative", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tag.GamePct < 30) continue;

            var tagName = tag.Name;
            return new ObjectiveSuggestion
            {
                Title = $"Address: {tagName}",
                SkillArea = "Gameplay pattern",
                Type = "primary",
                CompletionCriteria = $"Reduce '{tagName}' tag to under 20% of games",
                Description = $"You've tagged '{tagName}' in {tag.GamePct:F0}% of your recent games. " +
                    "This pattern is worth focusing on \u2014 identify the specific " +
                    "situations where it happens and develop a plan to avoid them.",
                Reason = $"'{tagName}' appears in {tag.GamePct:F0}% of games",
                Confidence = Math.Min(0.85, tag.GamePct / 60),
            };
        }
        return null;
    }

    // ── Rule 6: Losing matchups ─────────────────────────────────────

    private static ObjectiveSuggestion? CheckLosingMatchups(PlayerProfile profile)
    {
        foreach (var m in profile.Matchups)
        {
            if (m.Games < 3 || m.Winrate >= 40) continue;

            return new ObjectiveSuggestion
            {
                Title = $"Improve {m.ChampionName} vs {m.EnemyLaner}",
                SkillArea = "Matchup knowledge",
                Type = "primary",
                CompletionCriteria = $"Win 40%+ of {m.ChampionName} vs {m.EnemyLaner} games",
                Description = $"You're {m.Winrate:F0}% WR in {m.Games} games as {m.ChampionName} vs {m.EnemyLaner}. " +
                    "Study the matchup \u2014 when are your power spikes? " +
                    "What abilities to watch for? Write matchup notes after each game.",
                Reason = $"{m.ChampionName} vs {m.EnemyLaner}: {m.Winrate:F0}% WR over {m.Games} games",
                Confidence = Math.Min(0.8, (40 - m.Winrate) / 40),
            };
        }
        return null;
    }

    // ── Rule 7: Spotted problems ────────────────────────────────────

    private static ObjectiveSuggestion? CheckSpottedProblems(PlayerProfile profile)
    {
        foreach (var p in profile.SpottedProblems)
        {
            if (p.Count < 3) continue;

            var text = p.Text;
            return new ObjectiveSuggestion
            {
                Title = $"Address: {(text.Length > 50 ? text[..50] : text)}",
                SkillArea = "Self-identified",
                Type = "primary",
                CompletionCriteria = $"Resolve: {(text.Length > 80 ? text[..80] : text)}",
                Description = $"You've noted this problem in {p.Count} reviews: \"{(text.Length > 120 ? text[..120] : text)}\". " +
                    "Since you keep spotting it, making it a focused objective " +
                    "could help you systematically improve.",
                Reason = $"Noted in {p.Count} game reviews",
                Confidence = Math.Min(0.85, (double)p.Count / 8),
            };
        }
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string ObjStr(Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is string s ? s : "";

    private static int ObjInt(Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is int i ? i
         : d.TryGetValue(key, out var v2) && v2 is long l ? (int)l
         : 0;
}

#nullable enable

using System.Globalization;
using Microsoft.Data.Sqlite;
using Revu.Core.Constants;

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD + violation checking for user-defined gaming rules.</summary>
public sealed class RulesRepository : IRulesRepository
{
    private readonly IDbConnectionFactory _factory;
    private RulesSchema? _cachedSchema;

    public RulesRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(string name, string description = "", string ruleType = "custom",
        string conditionValue = "")
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        var columnNames = new List<string>();
        var values = new List<string>();

        if (schema.HasTitle)
        {
            columnNames.Add("title");
            values.Add("@title");
            cmd.Parameters.AddWithValue("@title", name);
        }

        if (schema.HasName)
        {
            columnNames.Add("name");
            values.Add("@name");
            cmd.Parameters.AddWithValue("@name", name);
        }

        if (schema.HasDescription)
        {
            columnNames.Add("description");
            values.Add("@description");
            cmd.Parameters.AddWithValue("@description", description);
        }

        if (schema.HasRuleType)
        {
            columnNames.Add("rule_type");
            values.Add("@ruleType");
            cmd.Parameters.AddWithValue("@ruleType", ruleType);
        }

        if (schema.HasConditionValue)
        {
            columnNames.Add("condition_value");
            values.Add("@conditionValue");
            cmd.Parameters.AddWithValue("@conditionValue", conditionValue);
        }

        if (schema.HasIsActive)
        {
            columnNames.Add("is_active");
            values.Add("1");
        }

        if (schema.HasStatus)
        {
            columnNames.Add("status");
            values.Add("'active'");
        }

        if (schema.HasCreatedAt)
        {
            columnNames.Add("created_at");
            values.Add("@createdAt");
            cmd.Parameters.AddWithValue("@createdAt", createdAt);
        }

        cmd.CommandText = $"""
            INSERT INTO rules
                ({string.Join(", ", columnNames)})
            VALUES ({string.Join(", ", values)})
            """;
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<RuleRecord>> GetAllAsync()
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCanonicalSelect(schema) + " ORDER BY is_active DESC, created_at DESC";
        return await ReadAllAsync(cmd);
    }

    public async Task<IReadOnlyList<RuleRecord>> GetActiveAsync()
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCanonicalSelect(schema) +
                          $" WHERE {BuildIsActiveExpression(schema)} = 1 ORDER BY created_at ASC";
        return await ReadAllAsync(cmd);
    }

    public async Task<RuleRecord?> GetAsync(long ruleId)
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCanonicalSelect(schema) + " WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);
        return await ReadSingleAsync(cmd);
    }

    public async Task ToggleAsync(long ruleId)
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);
        var isActiveExpr = BuildIsActiveExpression(schema);

        using var cmd = conn.CreateCommand();
        if (schema.HasIsActive && schema.HasStatus)
        {
            cmd.CommandText = $"""
                UPDATE rules
                SET
                    is_active = 1 - ({isActiveExpr}),
                    status = CASE
                        WHEN 1 - ({isActiveExpr}) = 1 THEN 'active'
                        ELSE 'inactive'
                    END
                WHERE id = @id
                """;
        }
        else if (schema.HasIsActive)
        {
            cmd.CommandText = $"""
                UPDATE rules
                SET is_active = 1 - ({isActiveExpr})
                WHERE id = @id
                """;
        }
        else if (schema.HasStatus)
        {
            cmd.CommandText = """
                UPDATE rules
                SET status = CASE
                    WHEN lower(COALESCE(status, 'active')) = 'active' THEN 'inactive'
                    ELSE 'active'
                END
                WHERE id = @id
                """;
        }
        else
        {
            return;
        }

        cmd.Parameters.AddWithValue("@id", ruleId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(long ruleId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rules WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateAsync(long ruleId, string name, string description, string ruleType, string conditionValue)
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);

        using var cmd = conn.CreateCommand();
        var sets = new List<string>();

        if (schema.HasTitle)
        {
            sets.Add("title = @name");
        }
        if (schema.HasName)
        {
            sets.Add("name = @name");
        }
        cmd.Parameters.AddWithValue("@name", name);

        if (schema.HasDescription)
        {
            sets.Add("description = @description");
            cmd.Parameters.AddWithValue("@description", description);
        }

        if (schema.HasRuleType)
        {
            sets.Add("rule_type = @ruleType");
            cmd.Parameters.AddWithValue("@ruleType", ruleType);
        }

        if (schema.HasConditionValue)
        {
            sets.Add("condition_value = @conditionValue");
            cmd.Parameters.AddWithValue("@conditionValue", conditionValue);
        }

        if (sets.Count == 0) return;

        cmd.CommandText = $"UPDATE rules SET {string.Join(", ", sets)} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<RuleViolation>> CheckViolationsAsync(
        IReadOnlyList<RuleCheckGame>? todaysGames = null,
        int? mentalRating = null)
    {
        var rules = await GetActiveAsync();
        var results = new List<RuleViolation>();
        var now = DateTime.Now;

        foreach (var rule in rules)
        {
            bool violated = false;
            string reason = "";

            switch (rule.RuleType)
            {
                case "no_play_day":
                {
                    var days = rule.ConditionValue.Split(',', StringSplitOptions.TrimEntries)
                        .Select(static day => day.ToLowerInvariant())
                        .ToList();
                    var todayName = now.ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant();
                    if (days.Contains(todayName))
                    {
                        violated = true;
                        reason = $"Today is {now.ToString("dddd", CultureInfo.InvariantCulture)}";
                    }

                    break;
                }

                case "no_play_after":
                {
                    if (int.TryParse(rule.ConditionValue, out var hour) && now.Hour >= hour)
                    {
                        violated = true;
                        reason = $"It's past {hour}:00";
                    }

                    break;
                }

                case "loss_streak" when todaysGames is not null:
                {
                    var (threshold, cooldownMinutes) = ParseLossStreakCondition(rule.ConditionValue);
                    if (threshold > 0)
                    {
                        // Walk back from the end to find the current tail streak of losses.
                        var streakStartIndex = todaysGames.Count;
                        for (var i = todaysGames.Count - 1; i >= 0; i--)
                        {
                            if (todaysGames[i].Win) break;
                            streakStartIndex = i;
                        }
                        var consecutive = todaysGames.Count - streakStartIndex;

                        if (consecutive >= threshold)
                        {
                            // Cooldown is armed by the loss that first tripped the threshold,
                            // not by the most recent loss. Further losses during the cooldown
                            // are the violation; they don't re-arm the timer.
                            var triggerTs = todaysGames[streakStartIndex + threshold - 1].Timestamp;

                            if (cooldownMinutes is int cd && cd > 0 && triggerTs > 0)
                            {
                                var nowUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
                                var elapsedMin = (nowUnix - triggerTs) / 60;
                                var remainingMin = cd - elapsedMin;
                                if (remainingMin > 0)
                                {
                                    violated = true;
                                    reason = remainingMin >= 60
                                        ? $"{consecutive} consecutive losses — cooldown ends in {remainingMin / 60}h {remainingMin % 60}m"
                                        : $"{consecutive} consecutive losses — cooldown ends in {remainingMin}m";
                                }
                            }
                            else
                            {
                                violated = true;
                                reason = $"{consecutive} consecutive losses";
                            }
                        }
                    }

                    break;
                }

                case "max_games" when todaysGames is not null:
                {
                    // todaysGames is games already logged today (before this game).
                    // If that count already equals or exceeds the limit, this game is the violation.
                    if (int.TryParse(rule.ConditionValue, out var maxGames) && todaysGames.Count >= maxGames)
                    {
                        violated = true;
                        reason = $"Already played {todaysGames.Count}/{maxGames} games today";
                    }

                    break;
                }

                case "min_mental" when mentalRating is not null:
                {
                    if (int.TryParse(rule.ConditionValue, out var minMental) && mentalRating.Value < minMental)
                    {
                        violated = true;
                        reason = $"Mental at {mentalRating.Value}, minimum is {minMental}";
                    }

                    break;
                }
            }

            results.Add(new RuleViolation(rule, violated, reason));
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<long, RuleEvidence>> GetRuleEvidenceAsync(
        IReadOnlyList<RuleRecord> rules)
    {
        // One pass over the visible game history; each rule's trigger
        // condition is evaluated behaviorally from outcomes/times — NOT from
        // session_log.rule_broken, which the user can (and does) clear.
        var games = await LoadEvidenceGamesAsync();

        var baselineWins = games.Count(static g => g.Win);
        var result = new Dictionary<long, RuleEvidence>();
        foreach (var rule in rules)
        {
            if (rule.RuleType == "custom") continue;
            var triggers = FindTriggerGames(rule, games);
            result[rule.Id] = new RuleEvidence(
                RuleId: rule.Id,
                TriggerGames: triggers.Count,
                TriggerWins: triggers.Count(static g => g.Win),
                LastTriggerDate: triggers.Count > 0 ? triggers[^1].Date : "",
                BaselineGames: games.Count,
                BaselineWins: baselineWins);
        }
        return result;
    }

    /// <summary>Local date the behavioral streak judgment shipped (v2.17.31).
    /// Days before it are judged by the standard that existed then —
    /// surviving non-skipped rule_broken flags — so the re-base never
    /// retroactively erases a streak the player had already earned.</summary>
    private const string BehavioralRebaseEpochDate = "2026-06-12";

    public async Task<int> GetBehavioralAdherenceStreakAsync(string? behavioralSinceDate = null)
    {
        // P2a re-base (user decision 2026-06-12): the streak mechanic stays,
        // but it counts behavioral trips — games played while an active
        // rule's condition held — so flag housekeeping can neither fake the
        // number nor break it. A rule only judges games played after it was
        // created; play-days before the first rule are out of scope. The
        // same no-retroactivity principle applies to the judge itself: days
        // before the epoch keep their flag-era verdicts.
        var rules = (await GetActiveAsync())
            .Where(static r => r.RuleType != "custom")
            .ToList();
        if (rules.Count == 0) return 0;

        var firstRuleCreated = rules.Min(static r => r.CreatedAt ?? long.MaxValue);
        if (firstRuleCreated == long.MaxValue) return 0;
        var sinceDate = DateTimeOffset.FromUnixTimeSeconds(firstRuleCreated)
            .ToLocalTime().ToString("yyyy-MM-dd");

        var games = await LoadEvidenceGamesAsync();
        if (games.Count == 0) return 0;

        // Skipped games are streak-neutral (user decision 2026-06-12): skip
        // is the player's explicit "this one doesn't count" lever, and using
        // it to protect the streak is fine — the streak is a motivation
        // mechanic. The per-rule records in GetRuleEvidenceAsync deliberately
        // do NOT honor this: the instrument stays honest while the mechanic
        // forgives. (Those records also ignore the epoch — full-history
        // behavioral analysis is their whole job.)
        var epoch = behavioralSinceDate ?? BehavioralRebaseEpochDate;
        var triggerDates = new HashSet<string>(StringComparer.Ordinal);

        // Pre-epoch days: flag-era judgment (non-skipped surviving flags).
        foreach (var game in games)
        {
            if (string.CompareOrdinal(game.Date, epoch) < 0
                && game.RuleBroken && !game.Skipped)
            {
                triggerDates.Add(game.Date);
            }
        }

        // Epoch onward: behavioral judgment.
        foreach (var rule in rules)
        {
            foreach (var trigger in FindTriggerGames(rule, games))
            {
                if (trigger.Skipped) continue;
                if (string.CompareOrdinal(trigger.Date, epoch) < 0) continue;
                if (rule.CreatedAt is not long created || trigger.Ts >= created)
                {
                    triggerDates.Add(trigger.Date);
                }
            }
        }

        var streak = 0;
        foreach (var day in games.Select(static g => g.Date)
                     .Distinct()
                     .OrderByDescending(static d => d, StringComparer.Ordinal))
        {
            if (string.CompareOrdinal(day, sinceDate) < 0) break;
            if (triggerDates.Contains(day)) break;
            streak++;
        }
        return streak;
    }

    private async Task<List<EvidenceGame>> LoadEvidenceGamesAsync()
    {
        var games = new List<EvidenceGame>();
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.timestamp, g.win, sl.mental_rating, COALESCE(sl.is_skipped, 0),
                   COALESCE(sl.rule_broken, 0)
            FROM games g
            LEFT JOIN session_log sl ON sl.game_id = g.game_id
            WHERE COALESCE(g.is_hidden, 0) = 0 AND g.timestamp > 0
            ORDER BY g.timestamp ASC
            """;
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var ts = reader.GetInt64(0);
            var local = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
            var skipped = !reader.IsDBNull(3) && reader.GetInt64(3) != 0;
            games.Add(new EvidenceGame(
                Ts: ts,
                Date: local.ToString("yyyy-MM-dd"),
                Hour: local.Hour,
                DayName: local.ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant(),
                Win: !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                // Skipped games are excluded from mental stats by app
                // convention — their self-report must not arm min_mental.
                Mental: skipped || reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Skipped: skipped,
                // Flag-era verdict; only the streak's pre-epoch branch reads it.
                RuleBroken: !reader.IsDBNull(4) && reader.GetInt64(4) != 0));
        }
        return games;
    }

    /// <summary>
    /// Games played while <paramref name="rule"/>'s condition already held —
    /// the historical reconstruction of the live <see cref="CheckViolationsAsync"/>
    /// check (cooldowns ignored for loss_streak: the record asks "queued in the
    /// condition at all?"). Faithful for conditions whose live inputs replay
    /// from game history (loss_streak, max_games, no_play_after, no_play_day).
    ///
    /// min_mental is intentionally NOT a mirror of the live check. The live
    /// check warns pre-game on the current mental ("don't queue below N"), which
    /// fires for most games (the player's average sits near the threshold) and —
    /// when reconstructed from the PRIOR game's post-game rating — punished
    /// ordinary one-off bad games and even games played after a long break
    /// (P-015). The user's rule, simplified: a genuinely tilted game (mental ≤
    /// <see cref="GameConstants.MentalTiltedFloor"/>) requires a
    /// <see cref="GameConstants.TiltCooloffSeconds"/> cool-off; requeueing
    /// inside that window is the trip. The rule's configured threshold no longer
    /// drives the streak — the hard tilt floor does — so the streak only breaks
    /// on real "kept playing while tilted" behavior, not on every sub-threshold
    /// rating.
    /// </summary>
    private static List<EvidenceGame> FindTriggerGames(RuleRecord rule, List<EvidenceGame> games)
    {
        var triggers = new List<EvidenceGame>();
        var currentDate = "";
        var consecutiveLosses = 0;
        var gamesToday = 0;
        long previousTs = 0;
        int? previousMental = null;

        foreach (var game in games)
        {
            if (game.Date != currentDate)
            {
                currentDate = game.Date;
                consecutiveLosses = 0;
                gamesToday = 0;
            }

            var triggered = rule.RuleType switch
            {
                "loss_streak" => ParseLossStreakCondition(rule.ConditionValue).Threshold is int t
                    && t > 0 && consecutiveLosses >= t,
                "max_games" => int.TryParse(rule.ConditionValue, out var maxGames)
                    && maxGames > 0 && gamesToday >= maxGames,
                // Trip = previous game was tilted (≤ floor) AND this game was
                // played inside the required cool-off window. A break clears it;
                // a non-tilted prior game never arms it.
                "min_mental" => previousMental is int pm
                    && pm > 0 && pm <= GameConstants.MentalTiltedFloor
                    && previousTs > 0
                    && game.Ts - previousTs < GameConstants.TiltCooloffSeconds,
                "no_play_after" => int.TryParse(rule.ConditionValue, out var hour)
                    && game.Hour >= hour,
                "no_play_day" => rule.ConditionValue
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(day => string.Equals(day, game.DayName, StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
            if (triggered) triggers.Add(game);

            consecutiveLosses = game.Win ? 0 : consecutiveLosses + 1;
            gamesToday++;
            previousMental = game.Mental;
            previousTs = game.Ts;
        }

        return triggers;
    }

    private sealed record EvidenceGame(long Ts, string Date, int Hour, string DayName, bool Win, int? Mental, bool Skipped, bool RuleBroken);

    /// <summary>
    /// Parses a loss_streak condition value. Format: "X" (threshold only, no cooldown →
    /// rest-of-day) or "X:Y" where Y is cooldown minutes after the last loss.
    /// </summary>
    public static (int Threshold, int? CooldownMinutes) ParseLossStreakCondition(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, null);
        var parts = raw.Split(':', 2);
        if (!int.TryParse(parts[0].Trim(), out var threshold) || threshold <= 0) return (0, null);
        if (parts.Length == 1 || string.IsNullOrWhiteSpace(parts[1])) return (threshold, null);
        if (int.TryParse(parts[1].Trim(), out var cd) && cd > 0) return (threshold, cd);
        return (threshold, null);
    }

    private static string BuildCanonicalSelect(RulesSchema schema)
    {
        return $"""
            SELECT
                id,
                {BuildNameExpression(schema)} AS name,
                {BuildDescriptionExpression(schema)} AS description,
                {BuildRuleTypeExpression(schema)} AS rule_type,
                {BuildConditionValueExpression(schema)} AS condition_value,
                {BuildIsActiveExpression(schema)} AS is_active,
                {BuildCreatedAtExpression(schema)} AS created_at
            FROM rules
            """;
    }

    private static string BuildNameExpression(RulesSchema schema)
    {
        if (schema.HasName && schema.HasTitle)
        {
            return "COALESCE(NULLIF(name, ''), title, '')";
        }

        if (schema.HasName)
        {
            return "COALESCE(name, '')";
        }

        if (schema.HasTitle)
        {
            return "COALESCE(title, '')";
        }

        return "''";
    }

    private static string BuildDescriptionExpression(RulesSchema schema)
    {
        return schema.HasDescription ? "COALESCE(description, '')" : "''";
    }

    private static string BuildRuleTypeExpression(RulesSchema schema)
    {
        return schema.HasRuleType ? "COALESCE(NULLIF(rule_type, ''), 'custom')" : "'custom'";
    }

    private static string BuildConditionValueExpression(RulesSchema schema)
    {
        return schema.HasConditionValue ? "COALESCE(condition_value, '')" : "''";
    }

    private static string BuildIsActiveExpression(RulesSchema schema)
    {
        if (schema.HasIsActive && schema.HasStatus)
        {
            return "COALESCE(is_active, CASE WHEN lower(COALESCE(status, 'active')) = 'active' THEN 1 ELSE 0 END)";
        }

        if (schema.HasIsActive)
        {
            return "COALESCE(is_active, 1)";
        }

        if (schema.HasStatus)
        {
            return "CASE WHEN lower(COALESCE(status, 'active')) = 'active' THEN 1 ELSE 0 END";
        }

        return "1";
    }

    private static string BuildCreatedAtExpression(RulesSchema schema)
    {
        return schema.HasCreatedAt ? "created_at" : "NULL";
    }

    private async Task<RulesSchema> GetSchemaAsync(SqliteConnection connection)
    {
        if (_cachedSchema is not null)
            return _cachedSchema;

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(rules)";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        _cachedSchema = new RulesSchema(
            HasTitle: columns.Contains("title"),
            HasName: columns.Contains("name"),
            HasDescription: columns.Contains("description"),
            HasStatus: columns.Contains("status"),
            HasRuleType: columns.Contains("rule_type"),
            HasConditionValue: columns.Contains("condition_value"),
            HasIsActive: columns.Contains("is_active"),
            HasCreatedAt: columns.Contains("created_at"));

        return _cachedSchema;
    }

    private static async Task<IReadOnlyList<RuleRecord>> ReadAllAsync(SqliteCommand cmd)
    {
        var results = new List<RuleRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadRule(reader));
        }

        return results;
    }

    private static async Task<RuleRecord?> ReadSingleAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRule(reader) : null;
    }

    private static RuleRecord ReadRule(SqliteDataReader reader)
    {
        return new RuleRecord(
            Id: reader.IsDBNull(reader.GetOrdinal("id")) ? 0 : reader.GetInt64(reader.GetOrdinal("id")),
            Name: reader.IsDBNull(reader.GetOrdinal("name")) ? "" : reader.GetString(reader.GetOrdinal("name")),
            Description: reader.IsDBNull(reader.GetOrdinal("description")) ? "" : reader.GetString(reader.GetOrdinal("description")),
            RuleType: reader.IsDBNull(reader.GetOrdinal("rule_type")) ? "custom" : reader.GetString(reader.GetOrdinal("rule_type")),
            ConditionValue: reader.IsDBNull(reader.GetOrdinal("condition_value")) ? "" : reader.GetString(reader.GetOrdinal("condition_value")),
            IsActive: !reader.IsDBNull(reader.GetOrdinal("is_active")) && reader.GetInt64(reader.GetOrdinal("is_active")) != 0,
            CreatedAt: reader.IsDBNull(reader.GetOrdinal("created_at")) ? null : reader.GetInt64(reader.GetOrdinal("created_at")));
    }

    private sealed record RulesSchema(
        bool HasTitle,
        bool HasName,
        bool HasDescription,
        bool HasStatus,
        bool HasRuleType,
        bool HasConditionValue,
        bool HasIsActive,
        bool HasCreatedAt);
}

#nullable enable

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD + violation checking for user-defined gaming rules.</summary>
public sealed class RulesRepository : IRulesRepository
{
    private readonly IDbConnectionFactory _factory;

    public RulesRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(string name, string description = "", string ruleType = "custom",
        string conditionValue = "")
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rules
                (name, description, rule_type, condition_value, is_active, created_at)
            VALUES (@name, @description, @ruleType, @conditionValue, 1, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@ruleType", ruleType);
        cmd.Parameters.AddWithValue("@conditionValue", conditionValue);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM rules ORDER BY is_active DESC, created_at DESC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetActiveAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM rules WHERE is_active = 1 ORDER BY created_at ASC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<Dictionary<string, object?>?> GetAsync(long ruleId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM rules WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);
        return await ReadSingleRowAsync(cmd);
    }

    public async Task ToggleAsync(long ruleId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rules SET is_active = 1 - is_active WHERE id = @id";
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

    public async Task<IReadOnlyList<RuleViolation>> CheckViolationsAsync(
        IReadOnlyList<Dictionary<string, object?>>? todaysGames = null,
        int? mentalRating = null)
    {
        var rules = await GetActiveAsync();
        var results = new List<RuleViolation>();
        var now = DateTime.Now;

        foreach (var rule in rules)
        {
            var ruleType = rule.TryGetValue("rule_type", out var rt) ? rt?.ToString() ?? "" : "";
            var conditionValue = rule.TryGetValue("condition_value", out var cv) ? cv?.ToString() ?? "" : "";
            bool violated = false;
            string reason = "";

            switch (ruleType)
            {
                case "no_play_day":
                {
                    var days = conditionValue.Split(',', StringSplitOptions.TrimEntries)
                        .Select(d => d.ToLowerInvariant())
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
                    if (int.TryParse(conditionValue, out int hour) && now.Hour >= hour)
                    {
                        violated = true;
                        reason = $"It's past {hour}:00";
                    }
                    break;
                }

                case "loss_streak" when todaysGames is not null:
                {
                    if (int.TryParse(conditionValue, out int threshold))
                    {
                        int consecutive = 0;
                        for (int i = todaysGames.Count - 1; i >= 0; i--)
                        {
                            var game = todaysGames[i];
                            bool isWin = game.TryGetValue("win", out var w) && w is not null &&
                                         (w is bool b ? b : Convert.ToInt64(w) != 0);
                            if (!isWin)
                                consecutive++;
                            else
                                break;
                        }
                        if (consecutive >= threshold)
                        {
                            violated = true;
                            reason = $"{consecutive} consecutive losses";
                        }
                    }
                    break;
                }

                case "max_games" when todaysGames is not null:
                {
                    if (int.TryParse(conditionValue, out int maxGames) && todaysGames.Count >= maxGames)
                    {
                        violated = true;
                        reason = $"{todaysGames.Count}/{maxGames} games played";
                    }
                    break;
                }

                case "min_mental" when mentalRating is not null:
                {
                    if (int.TryParse(conditionValue, out int minMental) && mentalRating.Value < minMental)
                    {
                        violated = true;
                        reason = $"Mental at {mentalRating.Value}, minimum is {minMental}";
                    }
                    break;
                }
                // custom rules can't be auto-checked
            }

            results.Add(new RuleViolation(rule, violated, reason));
        }

        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAllRowsAsync(SqliteCommand cmd)
    {
        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadRow(reader));
        }
        return results;
    }

    private static async Task<Dictionary<string, object?>?> ReadSingleRowAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRow(reader) : null;
    }

    private static Dictionary<string, object?> ReadRow(SqliteDataReader reader)
    {
        var dict = new Dictionary<string, object?>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return dict;
    }
}

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

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAllAsync()
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCanonicalSelect(schema) + " ORDER BY is_active DESC, created_at DESC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetActiveAsync()
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCanonicalSelect(schema) +
                          $" WHERE {BuildIsActiveExpression(schema)} = 1 ORDER BY created_at ASC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<Dictionary<string, object?>?> GetAsync(long ruleId)
    {
        using var conn = _factory.CreateConnection();
        var schema = await GetSchemaAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCanonicalSelect(schema) + " WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);
        return await ReadSingleRowAsync(cmd);
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

    private static async Task<RulesSchema> GetSchemaAsync(SqliteConnection connection)
    {
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

        return new RulesSchema(
            HasTitle: columns.Contains("title"),
            HasName: columns.Contains("name"),
            HasDescription: columns.Contains("description"),
            HasStatus: columns.Contains("status"),
            HasRuleType: columns.Contains("rule_type"),
            HasConditionValue: columns.Contains("condition_value"),
            HasIsActive: columns.Contains("is_active"),
            HasCreatedAt: columns.Contains("created_at"));
    }

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

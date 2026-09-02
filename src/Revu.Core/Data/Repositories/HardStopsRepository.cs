#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>Per-rule hard-stop record over a window: interventions the enforcer
/// made (queue cancelled / ready check declined) and explicit overrides.</summary>
public sealed record HardStopCounts(int Held, int Overridden);

/// <summary>
/// v3.7 (schema v14): the hard_stops intervention log. One row per time the
/// enforcer acted on a tripped rule, and one per override the player chose.
/// Behavioral record, never a score — it feeds the Rules page line
/// ("HELD 3× · OVERRIDDEN 1×") and the enforcer's "overridden today" check.
/// </summary>
public interface IHardStopsRepository
{
    /// <summary>Append one row. <paramref name="action"/> is a
    /// <see cref="Services.HardStopActions"/> value. Returns the new row id.</summary>
    Task<long> RecordAsync(long ruleId, string action, string reason, long? createdAt = null);

    /// <summary>Rule ids with an override row at or after <paramref name="sinceUnix"/>
    /// (the enforcer passes local start-of-day: an override lasts the rest of the day).</summary>
    Task<IReadOnlySet<long>> GetOverriddenRuleIdsAsync(long sinceUnix);

    /// <summary>Held / overridden counts per rule at or after <paramref name="sinceUnix"/>.
    /// Rules with no rows are absent from the result.</summary>
    Task<IReadOnlyDictionary<long, HardStopCounts>> GetCountsAsync(long sinceUnix);
}

public sealed class HardStopsRepository : IHardStopsRepository
{
    private readonly IDbConnectionFactory _factory;

    public HardStopsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> RecordAsync(long ruleId, string action, string reason, long? createdAt = null)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hard_stops (rule_id, action, reason, created_at)
            VALUES (@ruleId, @action, @reason, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@ruleId", ruleId);
        cmd.Parameters.AddWithValue("@action", (action ?? "").Trim());
        cmd.Parameters.AddWithValue("@reason", reason ?? "");
        cmd.Parameters.AddWithValue("@createdAt", createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlySet<long>> GetOverriddenRuleIdsAsync(long sinceUnix)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT rule_id
            FROM hard_stops
            WHERE action = @override AND created_at >= @since
            """;
        cmd.Parameters.AddWithValue("@override", Services.HardStopActions.Override);
        cmd.Parameters.AddWithValue("@since", sinceUnix);

        var ids = new HashSet<long>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0)) ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<IReadOnlyDictionary<long, HardStopCounts>> GetCountsAsync(long sinceUnix)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rule_id,
                   SUM(CASE WHEN action = @override THEN 0 ELSE 1 END) AS held,
                   SUM(CASE WHEN action = @override THEN 1 ELSE 0 END) AS overridden
            FROM hard_stops
            WHERE created_at >= @since
            GROUP BY rule_id
            """;
        cmd.Parameters.AddWithValue("@override", Services.HardStopActions.Override);
        cmd.Parameters.AddWithValue("@since", sinceUnix);

        var map = new Dictionary<long, HardStopCounts>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0)) continue;
            map[reader.GetInt64(0)] = new HardStopCounts(
                Held: reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1),
                Overridden: reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2));
        }
        return map;
    }
}

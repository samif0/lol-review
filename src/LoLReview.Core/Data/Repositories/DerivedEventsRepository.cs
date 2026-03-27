#nullable enable

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD + computation for derived event definitions and instances.</summary>
public sealed class DerivedEventsRepository : IDerivedEventsRepository
{
    private readonly IDbConnectionFactory _factory;

    public DerivedEventsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(string name, IReadOnlyList<string> sourceTypes, int minCount,
        int windowSeconds, string color = "#ff6b6b")
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO derived_event_definitions
                (name, source_types, min_count, window_seconds, color, created_at)
            VALUES (@name, @sourceTypes, @minCount, @windowSeconds, @color, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@sourceTypes", JsonSerializer.Serialize(sourceTypes));
        cmd.Parameters.AddWithValue("@minCount", minCount);
        cmd.Parameters.AddWithValue("@windowSeconds", windowSeconds);
        cmd.Parameters.AddWithValue("@color", color);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAllDefinitionsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM derived_event_definitions ORDER BY is_default DESC, name ASC";

        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dict = ReadRow(reader);
            // Parse source_types JSON
            if (dict.TryGetValue("source_types", out var stObj) && stObj is string stStr)
            {
                try
                {
                    dict["source_types"] = JsonSerializer.Deserialize<List<string>>(stStr) ?? new List<string>();
                }
                catch
                {
                    dict["source_types"] = new List<string>();
                }
            }
            else
            {
                dict["source_types"] = new List<string>();
            }
            results.Add(dict);
        }
        return results;
    }

    public async Task DeleteDefinitionAsync(long definitionId)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var cmd1 = conn.CreateCommand())
        {
            cmd1.CommandText = "DELETE FROM derived_event_instances WHERE definition_id = @defId";
            cmd1.Parameters.AddWithValue("@defId", definitionId);
            cmd1.Transaction = transaction;
            await cmd1.ExecuteNonQueryAsync();
        }

        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = "DELETE FROM derived_event_definitions WHERE id = @defId AND is_default = 0";
            cmd2.Parameters.AddWithValue("@defId", definitionId);
            cmd2.Transaction = transaction;
            await cmd2.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public IReadOnlyList<Dictionary<string, object?>> ComputeInstances(
        long gameId,
        IReadOnlyList<Dictionary<string, object?>> events,
        IReadOnlyList<Dictionary<string, object?>> definitions)
    {
        var allInstances = new List<Dictionary<string, object?>>();

        foreach (var defn in definitions)
        {
            var sourceTypes = new HashSet<string>();
            if (defn.TryGetValue("source_types", out var stObj))
            {
                if (stObj is IEnumerable<string> stList)
                {
                    foreach (var s in stList) sourceTypes.Add(s);
                }
                else if (stObj is List<object> stObjList)
                {
                    foreach (var s in stObjList) sourceTypes.Add(s?.ToString() ?? "");
                }
            }

            int minCount = defn.TryGetValue("min_count", out var mc) ? Convert.ToInt32(mc) : 1;
            int window = defn.TryGetValue("window_seconds", out var ws) ? Convert.ToInt32(ws) : 30;

            // Filter and sort matching events
            var matching = events
                .Where(e => e.TryGetValue("event_type", out var et) && sourceTypes.Contains(et?.ToString() ?? ""))
                .OrderBy(e => e.TryGetValue("game_time_s", out var gts) ? Convert.ToInt64(gts) : 0L)
                .ToList();

            if (matching.Count < minCount)
                continue;

            int i = 0;
            while (i < matching.Count)
            {
                long startTime = matching[i].TryGetValue("game_time_s", out var st) ? Convert.ToInt64(st) : 0;
                var cluster = new List<Dictionary<string, object?>> { matching[i] };
                int j = i + 1;

                while (j < matching.Count)
                {
                    long t = matching[j].TryGetValue("game_time_s", out var gt) ? Convert.ToInt64(gt) : 0;
                    if (t - startTime <= window)
                    {
                        cluster.Add(matching[j]);
                        j++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (cluster.Count >= minCount)
                {
                    long endTime = cluster[^1].TryGetValue("game_time_s", out var et) ? Convert.ToInt64(et) : 0;
                    var sourceIds = cluster
                        .Where(e => e.TryGetValue("id", out var id) && id is not null && Convert.ToInt64(id) != 0)
                        .Select(e => Convert.ToInt64(e["id"]))
                        .ToList();

                    allInstances.Add(new Dictionary<string, object?>
                    {
                        ["game_id"] = gameId,
                        ["definition_id"] = defn.TryGetValue("id", out var did) ? did : 0,
                        ["start_time_s"] = startTime,
                        ["end_time_s"] = endTime,
                        ["event_count"] = cluster.Count,
                        ["source_event_ids"] = sourceIds,
                        ["definition_name"] = defn.TryGetValue("name", out var dn) ? dn : "",
                        ["color"] = defn.TryGetValue("color", out var clr) ? clr : "#ff6b6b",
                    });

                    i = j; // Advance past cluster (greedy non-overlapping)
                }
                else
                {
                    i++;
                }
            }
        }

        return allInstances;
    }

    public async Task SaveInstancesAsync(long gameId, IReadOnlyList<Dictionary<string, object?>> instances)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM derived_event_instances WHERE game_id = @gameId";
            delCmd.Parameters.AddWithValue("@gameId", gameId);
            delCmd.Transaction = transaction;
            await delCmd.ExecuteNonQueryAsync();
        }

        using (var insCmd = conn.CreateCommand())
        {
            insCmd.CommandText = """
                INSERT INTO derived_event_instances
                    (game_id, definition_id, start_time_s, end_time_s, event_count, source_event_ids)
                VALUES (@gameId, @defId, @startTimeS, @endTimeS, @eventCount, @sourceEventIds)
                """;
            insCmd.Transaction = transaction;

            var pGameId = insCmd.Parameters.Add("@gameId", SqliteType.Integer);
            var pDefId = insCmd.Parameters.Add("@defId", SqliteType.Integer);
            var pStartTimeS = insCmd.Parameters.Add("@startTimeS", SqliteType.Integer);
            var pEndTimeS = insCmd.Parameters.Add("@endTimeS", SqliteType.Integer);
            var pEventCount = insCmd.Parameters.Add("@eventCount", SqliteType.Integer);
            var pSourceEventIds = insCmd.Parameters.Add("@sourceEventIds", SqliteType.Text);

            foreach (var inst in instances)
            {
                pGameId.Value = gameId;
                pDefId.Value = inst.TryGetValue("definition_id", out var did) ? Convert.ToInt64(did) : 0;
                pStartTimeS.Value = inst.TryGetValue("start_time_s", out var sts) ? Convert.ToInt64(sts) : 0;
                pEndTimeS.Value = inst.TryGetValue("end_time_s", out var ets) ? Convert.ToInt64(ets) : 0;
                pEventCount.Value = inst.TryGetValue("event_count", out var ec) ? Convert.ToInt32(ec) : 0;

                if (inst.TryGetValue("source_event_ids", out var sids) && sids is not null)
                {
                    pSourceEventIds.Value = sids is string s ? s : JsonSerializer.Serialize(sids);
                }
                else
                {
                    pSourceEventIds.Value = "[]";
                }

                await insCmd.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetInstancesAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT di.*, dd.name as definition_name, dd.color, dd.source_types
            FROM derived_event_instances di
            JOIN derived_event_definitions dd ON dd.id = di.definition_id
            WHERE di.game_id = @gameId
            ORDER BY di.start_time_s ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);

        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dict = ReadRow(reader);

            // Parse JSON fields
            if (dict.TryGetValue("source_event_ids", out var sids) && sids is string sidsStr)
            {
                try { dict["source_event_ids"] = JsonSerializer.Deserialize<List<long>>(sidsStr) ?? new List<long>(); }
                catch { dict["source_event_ids"] = new List<long>(); }
            }
            else
            {
                dict["source_event_ids"] = new List<long>();
            }

            if (dict.TryGetValue("source_types", out var stObj) && stObj is string stStr)
            {
                try { dict["source_types"] = JsonSerializer.Deserialize<List<string>>(stStr) ?? new List<string>(); }
                catch { dict["source_types"] = new List<string>(); }
            }
            else
            {
                dict["source_types"] = new List<string>();
            }

            results.Add(dict);
        }
        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────

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

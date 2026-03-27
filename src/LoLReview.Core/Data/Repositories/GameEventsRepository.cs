#nullable enable

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for game_events table -- timestamped in-game events.</summary>
public sealed class GameEventsRepository : IGameEventsRepository
{
    private readonly IDbConnectionFactory _factory;

    public GameEventsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task SaveEventsAsync(long gameId, IReadOnlyList<Dictionary<string, object?>> events)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        // Clear existing events
        using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM game_events WHERE game_id = @gameId";
            delCmd.Parameters.AddWithValue("@gameId", gameId);
            delCmd.Transaction = transaction;
            await delCmd.ExecuteNonQueryAsync();
        }

        // Bulk insert
        using (var insCmd = conn.CreateCommand())
        {
            insCmd.CommandText = """
                INSERT INTO game_events (game_id, event_type, game_time_s, details)
                VALUES (@gameId, @eventType, @gameTimeS, @details)
                """;
            insCmd.Transaction = transaction;

            var pGameId = insCmd.Parameters.Add("@gameId", SqliteType.Integer);
            var pEventType = insCmd.Parameters.Add("@eventType", SqliteType.Text);
            var pGameTimeS = insCmd.Parameters.Add("@gameTimeS", SqliteType.Integer);
            var pDetails = insCmd.Parameters.Add("@details", SqliteType.Text);

            foreach (var e in events)
            {
                pGameId.Value = gameId;
                pEventType.Value = e.TryGetValue("event_type", out var et) ? et?.ToString() ?? "" : "";
                pGameTimeS.Value = e.TryGetValue("game_time_s", out var gts) ? Convert.ToInt64(gts) : 0;

                if (e.TryGetValue("details", out var details) && details is not null)
                {
                    pDetails.Value = details is string s ? s : JsonSerializer.Serialize(details);
                }
                else
                {
                    pDetails.Value = "{}";
                }

                await insCmd.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetEventsAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM game_events
            WHERE game_id = @gameId
            ORDER BY game_time_s ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);

        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dict = ReadRow(reader);

            // Parse JSON details
            if (dict.TryGetValue("details", out var detailsObj) && detailsObj is string detailsStr)
            {
                try
                {
                    dict["details"] = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(detailsStr);
                }
                catch
                {
                    dict["details"] = new Dictionary<string, JsonElement>();
                }
            }
            else
            {
                dict["details"] = new Dictionary<string, JsonElement>();
            }

            results.Add(dict);
        }
        return results;
    }

    public async Task<bool> HasEventsAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM game_events WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    public async Task<int> GetEventCountAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM game_events WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task DeleteEventsAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM game_events WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
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

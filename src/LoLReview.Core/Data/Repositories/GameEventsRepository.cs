#nullable enable

using LoLReview.Core.Models;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for game_events table.</summary>
public sealed class GameEventsRepository : IGameEventsRepository
{
    private readonly IDbConnectionFactory _factory;

    public GameEventsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task SaveEventsAsync(long gameId, IReadOnlyList<GameEvent> events)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var deleteCommand = conn.CreateCommand())
        {
            deleteCommand.CommandText = "DELETE FROM game_events WHERE game_id = @gameId";
            deleteCommand.Parameters.AddWithValue("@gameId", gameId);
            deleteCommand.Transaction = transaction;
            await deleteCommand.ExecuteNonQueryAsync();
        }

        using var insertCommand = conn.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO game_events (game_id, event_type, game_time_s, details)
            VALUES (@gameId, @eventType, @gameTimeSeconds, @details)
            """;
        insertCommand.Transaction = transaction;

        var gameIdParameter = insertCommand.Parameters.Add("@gameId", SqliteType.Integer);
        var eventTypeParameter = insertCommand.Parameters.Add("@eventType", SqliteType.Text);
        var gameTimeParameter = insertCommand.Parameters.Add("@gameTimeSeconds", SqliteType.Integer);
        var detailsParameter = insertCommand.Parameters.Add("@details", SqliteType.Text);

        foreach (var gameEvent in events)
        {
            gameIdParameter.Value = gameId;
            eventTypeParameter.Value = gameEvent.EventType;
            gameTimeParameter.Value = gameEvent.GameTimeS;
            detailsParameter.Value = string.IsNullOrWhiteSpace(gameEvent.Details) ? "{}" : gameEvent.Details;
            await insertCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<GameEvent>> GetEventsAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, game_id, event_type, game_time_s, details
            FROM game_events
            WHERE game_id = @gameId
            ORDER BY game_time_s ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);

        var results = new List<GameEvent>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new GameEvent
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                GameId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                EventType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                GameTimeS = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Details = reader.IsDBNull(4) ? "{}" : reader.GetString(4),
            });
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
}

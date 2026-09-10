#nullable enable

using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD for game_events table. v3.11 (schema v16): every insert carries the row's
/// event_key and the three re-detection writers run the corrections applier inside their own
/// transaction (rules A, B, C) so a user's fix survives the detector that produced its subject.</summary>
public sealed class GameEventsRepository : IGameEventsRepository
{
    private const string InsertSql = """
        INSERT INTO game_events (game_id, event_type, game_time_s, details, event_key)
        VALUES (@gameId, @eventType, @gameTimeSeconds, @details, @eventKey)
        RETURNING id
        """;

    private readonly IDbConnectionFactory _factory;

    public GameEventsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task SaveEventsAsync(long gameId, IReadOnlyList<GameEvent> events)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        // Explicit review corrections survive capture replacement and keep their IDs.
        var reviewed = new List<(string OriginalType, int OriginalTime, int Start, int End)>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, event_type, game_time_s, details, event_key FROM game_events WHERE game_id=@game";
            read.Parameters.AddWithValue("@game", gameId);
            using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var data = ReviewedEncountersRepository.ReviewDetails(reader.IsDBNull(3) ? "{}" : reader.GetString(3));
                if (data is null) continue;
                var time = reader.GetInt32(2);
                reviewed.Add((data["original_type"]?.GetValue<string>() ?? reader.GetString(1),
                    data["original_time_s"]?.GetValue<int>() ?? time,
                    data["start_s"]?.GetValue<int>() ?? time, data["end_s"]?.GetValue<int>() ?? time));
            }
        }

        // Rule A: corrected rows (marker) and reviewed rows (legacy source) both survive the replace.
        using (var deleteCommand = conn.CreateCommand())
        {
            deleteCommand.CommandText = $"""
                DELETE FROM game_events WHERE game_id = @gameId
                AND {EventCorrectionSql.NotCorrectedPredicate}
                AND {EventCorrectionSql.NotReviewedPredicate}
                """;
            deleteCommand.Parameters.AddWithValue("@gameId", gameId);
            deleteCommand.Transaction = transaction;
            await deleteCommand.ExecuteNonQueryAsync();
        }

        var keys = EventIdentity.KeyForBatch(events);
        var plan = await EventCorrectionApplier.PlanIncomingAsync(conn, transaction, gameId, events, keys, EventCorrectionApplier.LiveTypes);

        using var insert = new RowInserter(conn, transaction, gameId);
        foreach (var d in plan.Decisions)
        {
            if (d.Action == IncomingAction.Suppress) continue;
            var gameEvent = d.Row;
            if (d.Action == IncomingAction.Insert)
            {
                if (ReviewedEncountersRepository.ReviewDetails(gameEvent.Details) is not null) continue;
                if (ReviewedEncountersRepository.IsEncounter(gameEvent.EventType)
                    && reviewed.Any(r => (r.OriginalType == gameEvent.EventType && r.OriginalTime == gameEvent.GameTimeS)
                        || (gameEvent.GameTimeS >= r.Start && gameEvent.GameTimeS <= r.End))) continue;
            }
            var newId = await insert.InsertAsync(gameEvent, d.EventKey);
            if (d.CorrectionRowId is not null) await plan.RecordInsertedAsync(conn, transaction, d, newId);
        }
        foreach (var x in plan.Extras)
        {
            var newId = await insert.InsertAsync(x.Row, x.EventKey);
            await plan.RecordInsertedAsync(conn, transaction, x, newId);
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<GameEvent>> GetEventsAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, game_id, event_type, game_time_s, details, event_key
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
                EventKey = reader.IsDBNull(5) ? null : reader.GetString(5),
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

    public async Task AppendEventsAsync(long gameId, IReadOnlyList<GameEvent> events)
    {
        if (events.Count == 0) return;

        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        // Rule B: the batch re-detects exactly the types it carries.
        var keys = EventIdentity.KeyForBatch(events);
        var scope = new HashSet<string>(events.Select(e => (e.EventType ?? "").ToUpperInvariant()), StringComparer.Ordinal);
        var plan = await EventCorrectionApplier.PlanIncomingAsync(conn, transaction, gameId, events, keys, scope);

        using var insert = new RowInserter(conn, transaction, gameId);
        foreach (var d in plan.Decisions)
        {
            if (d.Action == IncomingAction.Suppress) continue;
            var newId = await insert.InsertAsync(d.Row, d.EventKey);
            if (d.CorrectionRowId is not null) await plan.RecordInsertedAsync(conn, transaction, d, newId);
        }
        foreach (var x in plan.Extras)
        {
            var newId = await insert.InsertAsync(x.Row, x.EventKey);
            await plan.RecordInsertedAsync(conn, transaction, x, newId);
        }

        await transaction.CommitAsync();
    }

    public async Task DeleteEventsByTypeAsync(long gameId, string eventType)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM game_events WHERE game_id = @gameId AND event_type = @eventType
            AND {EventCorrectionSql.NotCorrectedPredicate}
            AND {EventCorrectionSql.NotReviewedPredicate}
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@eventType", eventType);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateEventDetailsAsync(int eventId, string details)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE game_events SET details = @details WHERE id = @id";
            cmd.Parameters.AddWithValue("@details", string.IsNullOrWhiteSpace(details) ? "{}" : details);
            cmd.Parameters.AddWithValue("@id", eventId);
            await cmd.ExecuteNonQueryAsync();
        }
        // Rule C: a detector stamp never overwrites a corrected attribute.
        await EventCorrectionApplier.ReapplyAttrsAsync(conn, transaction, eventId);
        await transaction.CommitAsync();
    }

    /// <summary>One prepared INSERT ... RETURNING id reused across a batch.</summary>
    private sealed class RowInserter : IDisposable
    {
        private readonly SqliteCommand _command;
        private readonly SqliteParameter _eventType;
        private readonly SqliteParameter _gameTime;
        private readonly SqliteParameter _details;
        private readonly SqliteParameter _eventKey;

        public RowInserter(SqliteConnection conn, SqliteTransaction transaction, long gameId)
        {
            _command = conn.CreateCommand();
            _command.CommandText = InsertSql;
            _command.Transaction = transaction;
            _command.Parameters.Add("@gameId", SqliteType.Integer).Value = gameId;
            _eventType = _command.Parameters.Add("@eventType", SqliteType.Text);
            _gameTime = _command.Parameters.Add("@gameTimeSeconds", SqliteType.Integer);
            _details = _command.Parameters.Add("@details", SqliteType.Text);
            _eventKey = _command.Parameters.Add("@eventKey", SqliteType.Text);
        }

        public async Task<long> InsertAsync(GameEvent gameEvent, string eventKey)
        {
            _eventType.Value = gameEvent.EventType;
            _gameTime.Value = gameEvent.GameTimeS;
            _details.Value = string.IsNullOrWhiteSpace(gameEvent.Details) ? "{}" : gameEvent.Details;
            _eventKey.Value = string.IsNullOrEmpty(eventKey) ? DBNull.Value : eventKey;
            return Convert.ToInt64(await _command.ExecuteScalarAsync());
        }

        public void Dispose() => _command.Dispose();
    }
}

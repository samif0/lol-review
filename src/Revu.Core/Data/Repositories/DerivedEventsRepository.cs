#nullable enable

using System.Text.Json;
using Revu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD + computation for derived event definitions and instances.</summary>
public sealed class DerivedEventsRepository : IDerivedEventsRepository
{
    private readonly IDbConnectionFactory _factory;

    public DerivedEventsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(
        string name,
        IReadOnlyList<string> sourceTypes,
        int minCount,
        int windowSeconds,
        string color = "#ff6b6b")
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

        using var idCommand = conn.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCommand.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<DerivedEventDefinitionRecord>> GetAllDefinitionsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, source_types, min_count, window_seconds, color, is_default, created_at
            FROM derived_event_definitions
            ORDER BY is_default DESC, name ASC
            """;

        var results = new List<DerivedEventDefinitionRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DerivedEventDefinitionRecord(
                Id: reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                Name: reader.IsDBNull(1) ? "" : reader.GetString(1),
                SourceTypes: DeserializeStringList(reader.IsDBNull(2) ? "[]" : reader.GetString(2)),
                MinCount: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                WindowSeconds: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Color: reader.IsDBNull(5) ? "#ff6b6b" : reader.GetString(5),
                IsDefault: !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
                CreatedAt: reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        return results;
    }

    public async Task DeleteDefinitionAsync(long definitionId)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var deleteInstancesCommand = conn.CreateCommand())
        {
            deleteInstancesCommand.CommandText = "DELETE FROM derived_event_instances WHERE definition_id = @definitionId";
            deleteInstancesCommand.Parameters.AddWithValue("@definitionId", definitionId);
            deleteInstancesCommand.Transaction = transaction;
            await deleteInstancesCommand.ExecuteNonQueryAsync();
        }

        using (var deleteDefinitionCommand = conn.CreateCommand())
        {
            deleteDefinitionCommand.CommandText = """
                DELETE FROM derived_event_definitions
                WHERE id = @definitionId AND is_default = 0
                """;
            deleteDefinitionCommand.Parameters.AddWithValue("@definitionId", definitionId);
            deleteDefinitionCommand.Transaction = transaction;
            await deleteDefinitionCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public IReadOnlyList<DerivedEventInstanceRecord> ComputeInstances(
        long gameId,
        IReadOnlyList<GameEvent> events,
        IReadOnlyList<DerivedEventDefinitionRecord> definitions)
    {
        var allInstances = new List<DerivedEventInstanceRecord>();

        foreach (var definition in definitions)
        {
            var sourceTypes = new HashSet<string>(definition.SourceTypes, StringComparer.OrdinalIgnoreCase);
            var matching = events
                .Where(gameEvent => sourceTypes.Contains(gameEvent.EventType))
                .OrderBy(gameEvent => gameEvent.GameTimeS)
                .ToList();

            if (matching.Count < definition.MinCount)
            {
                continue;
            }

            var index = 0;
            while (index < matching.Count)
            {
                var startTime = matching[index].GameTimeS;
                var cluster = new List<GameEvent> { matching[index] };
                var nextIndex = index + 1;

                while (nextIndex < matching.Count)
                {
                    var eventTime = matching[nextIndex].GameTimeS;
                    if (eventTime - startTime > definition.WindowSeconds)
                    {
                        break;
                    }

                    cluster.Add(matching[nextIndex]);
                    nextIndex++;
                }

                if (cluster.Count >= definition.MinCount)
                {
                    allInstances.Add(new DerivedEventInstanceRecord(
                        Id: 0,
                        GameId: gameId,
                        DefinitionId: definition.Id,
                        StartTimeSeconds: startTime,
                        EndTimeSeconds: cluster[^1].GameTimeS,
                        EventCount: cluster.Count,
                        SourceEventIds: cluster.Where(static item => item.Id > 0).Select(static item => (long)item.Id).ToList(),
                        DefinitionName: definition.Name,
                        Color: definition.Color,
                        SourceTypes: definition.SourceTypes));

                    index = nextIndex;
                    continue;
                }

                index++;
            }
        }

        return allInstances;
    }

    public async Task SaveInstancesAsync(long gameId, IReadOnlyList<DerivedEventInstanceRecord> instances)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var deleteCommand = conn.CreateCommand())
        {
            deleteCommand.CommandText = "DELETE FROM derived_event_instances WHERE game_id = @gameId";
            deleteCommand.Parameters.AddWithValue("@gameId", gameId);
            deleteCommand.Transaction = transaction;
            await deleteCommand.ExecuteNonQueryAsync();
        }

        using var insertCommand = conn.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO derived_event_instances
                (game_id, definition_id, start_time_s, end_time_s, event_count, source_event_ids)
            VALUES (@gameId, @definitionId, @startTimeSeconds, @endTimeSeconds, @eventCount, @sourceEventIds)
            """;
        insertCommand.Transaction = transaction;

        var gameIdParameter = insertCommand.Parameters.Add("@gameId", SqliteType.Integer);
        var definitionParameter = insertCommand.Parameters.Add("@definitionId", SqliteType.Integer);
        var startParameter = insertCommand.Parameters.Add("@startTimeSeconds", SqliteType.Integer);
        var endParameter = insertCommand.Parameters.Add("@endTimeSeconds", SqliteType.Integer);
        var countParameter = insertCommand.Parameters.Add("@eventCount", SqliteType.Integer);
        var sourceIdsParameter = insertCommand.Parameters.Add("@sourceEventIds", SqliteType.Text);

        foreach (var instance in instances)
        {
            gameIdParameter.Value = gameId;
            definitionParameter.Value = instance.DefinitionId;
            startParameter.Value = instance.StartTimeSeconds;
            endParameter.Value = instance.EndTimeSeconds;
            countParameter.Value = instance.EventCount;
            sourceIdsParameter.Value = JsonSerializer.Serialize(instance.SourceEventIds);
            await insertCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<DerivedEventInstanceRecord>> GetInstancesAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                di.id,
                di.game_id,
                di.definition_id,
                di.start_time_s,
                di.end_time_s,
                di.event_count,
                di.source_event_ids,
                dd.name,
                dd.color,
                dd.source_types
            FROM derived_event_instances di
            JOIN derived_event_definitions dd ON dd.id = di.definition_id
            WHERE di.game_id = @gameId
            ORDER BY di.start_time_s ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);

        var results = new List<DerivedEventInstanceRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DerivedEventInstanceRecord(
                Id: reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                GameId: reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                DefinitionId: reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                StartTimeSeconds: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                EndTimeSeconds: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                EventCount: reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                SourceEventIds: DeserializeLongList(reader.IsDBNull(6) ? "[]" : reader.GetString(6)),
                DefinitionName: reader.IsDBNull(7) ? "" : reader.GetString(7),
                Color: reader.IsDBNull(8) ? "#ff6b6b" : reader.GetString(8),
                SourceTypes: DeserializeStringList(reader.IsDBNull(9) ? "[]" : reader.GetString(9))));
        }

        return results;
    }

    private static IReadOnlyList<string> DeserializeStringList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<long> DeserializeLongList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<long>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

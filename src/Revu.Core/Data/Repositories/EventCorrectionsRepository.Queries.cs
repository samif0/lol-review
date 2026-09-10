#nullable enable

using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Revu.Core.Services;

namespace Revu.Core.Data.Repositories;

/// <summary>Read side of the ledger plus the rule G key stamp.</summary>
public sealed partial class EventCorrectionsRepository
{
    public const int ExportVersion = 1;

    public async Task<IReadOnlyList<EventCorrection>> GetForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {EventCorrectionSql.SelectColumns} FROM event_corrections
            WHERE game_id = @g AND op <> 'revert'
            ORDER BY created_at DESC, id DESC
            """;
        cmd.Parameters.AddWithValue("@g", gameId);
        return await ReadAllAsync(cmd);
    }

    public async Task<IReadOnlyList<EventCorrection>> GetActiveForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        return await EventCorrectionSql.LoadApplicableAsync(conn, null, gameId);
    }

    public async Task<int> CountActiveForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM event_corrections
            WHERE game_id = @g AND op <> 'revert' AND state IN ('active', 'absorbed', 'orphaned')
            """;
        cmd.Parameters.AddWithValue("@g", gameId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public async Task<IReadOnlyList<EventCorrection>> ExportItemsAsync(long? gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = gameId is > 0
            ? $"SELECT {EventCorrectionSql.SelectColumns} FROM event_corrections WHERE game_id = @g ORDER BY id ASC"
            : $"SELECT {EventCorrectionSql.SelectColumns} FROM event_corrections ORDER BY id ASC";
        if (gameId is > 0) cmd.Parameters.AddWithValue("@g", gameId.Value);
        return await ReadAllAsync(cmd);
    }

    /// <summary>Local, unredacted export: every ledger row (revert rows included) with the
    /// original snapshot and the cumulative patch, so a report can be reproduced by hand.</summary>
    public async Task<string> ExportAsync(long? gameId, string appVersion)
    {
        var rows = await ExportItemsAsync(gameId);
        var items = new JsonArray();
        foreach (var r in rows)
        {
            items.Add(new JsonObject
            {
                ["correction_id"] = r.CorrectionId,
                ["game_id"] = r.GameId,
                ["subject_key"] = r.SubjectKey,
                ["subject_type"] = r.SubjectType,
                ["subject_time_s"] = r.SubjectTimeS,
                ["op"] = r.Op,
                ["patch"] = JsonNode.Parse(r.Patch.ToJson()),
                ["original"] = JsonNode.Parse(r.Original.ToJson()),
                ["reason"] = r.Reason,
                ["detector"] = r.Detector,
                ["detector_v"] = r.DetectorVersion is { } dv ? JsonValue.Create(dv) : null,
                ["app_version"] = r.AppVersion,
                ["delta_s"] = r.DeltaS is { } ds ? JsonValue.Create(ds) : null,
                ["state"] = r.State,
                ["supersedes_id"] = r.SupersedesId is { } sid ? JsonValue.Create(sid) : null,
                ["rebased_from"] = r.RebasedFrom,
                ["created_at"] = r.CreatedAt,
                ["updated_at"] = r.UpdatedAt,
            });
        }
        var doc = new JsonObject
        {
            ["export_v"] = ExportVersion,
            ["app_version"] = appVersion ?? "",
            ["schema_v"] = Schema.EventCorrectionsSchemaVersion,
            ["exported_at"] = EventCorrectionSql.Now(),
            ["items"] = items,
        };
        return doc.ToJsonString();
    }

    /// <summary>Rule G, first half. Rows already keyed keep their key; rows sharing a base key with
    /// a keyed neighbour get the next ordinal because the whole game is keyed as ONE batch.</summary>
    public async Task<int> StampMissingEventKeysAsync(int limit)
    {
        if (limit <= 0) return 0;
        using var conn = _factory.CreateConnection();
        var games = new List<long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT game_id FROM game_events WHERE event_key IS NULL GROUP BY game_id ORDER BY game_id DESC";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) games.Add(reader.GetInt64(0));
        }

        var stamped = 0;
        foreach (var gameId in games)
        {
            if (stamped >= limit) break;
            using var tx = conn.BeginTransaction();
            var rows = await EventCorrectionSql.LoadRowsAsync(conn, tx, gameId);
            var batch = rows.Where(r => r.EventKey is null || r.EventKey.StartsWith(EventIdentity.DetectedPrefix, StringComparison.Ordinal)).ToList();
            var keys = EventIdentity.KeyForBatch(batch);
            for (var i = 0; i < batch.Count; i++)
            {
                if (batch[i].EventKey is not null) continue;
                await EventCorrectionSql.SetEventKeyAsync(conn, tx, batch[i].Id, keys[i]);
                stamped++;
            }
            await tx.CommitAsync();
        }
        return stamped;
    }

    private static async Task<IReadOnlyList<EventCorrection>> ReadAllAsync(SqliteCommand cmd)
    {
        var rows = new List<EventCorrection>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(EventCorrectionSql.Read(reader));
        return rows;
    }
}

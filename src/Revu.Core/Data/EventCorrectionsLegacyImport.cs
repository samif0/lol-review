#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Data;

/// <summary>
/// v16 one-shot: every legacy <c>details.source='reviewed_encounter'</c> row (the pre-ledger
/// explicit combat-moment correction) becomes a ledger row plus a marked, keyed game_events
/// row, so the new writers treat it exactly like a correction saved today. Runs inside the
/// migration hook, right after the event_key ALTER and before the version is recorded.
/// </summary>
public static class EventCorrectionsLegacyImport
{
    /// <summary>Convert every details.source='reviewed_encounter' row that has no correction marker
    /// into a ledger row + a marked, keyed game_events row. Idempotent (INSERT OR IGNORE on
    /// correction_id, marker check on the row). Per-row try/catch. Own transaction. Returns rows imported.</summary>
    public static async Task<int> RunAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        var rows = new List<(long Id, long GameId, string Type, int TimeS, string Details)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id, game_id, event_type, game_time_s, details FROM game_events
                WHERE json_valid(details)
                  AND json_extract(details, '$.source') = 'reviewed_encounter'
                  AND json_extract(details, '$.correction') IS NULL
                ORDER BY id
                """;
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3), reader.IsDBNull(4) ? "{}" : reader.GetString(4)));
            }
        }
        if (rows.Count == 0) return 0;

        var imported = 0;
        using var tx = connection.BeginTransaction();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ImportRowAsync(connection, tx, row.Id, row.GameId, row.Type, row.TimeS, row.Details)) imported++;
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or FormatException or ArgumentException)
            {
                CoreDiagnostics.WriteVerbose($"Legacy encounter import skipped row {row.Id}: {ex.Message}");
            }
        }
        await tx.CommitAsync(ct);
        return imported;
    }

    /// <summary>Deterministic id for rows without a request_id: Guid from the first 16 bytes of
    /// SHA256("legacy-encounter:" + rowId).</summary>
    public static Guid LegacyCorrectionId(long rowId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("legacy-encounter:" + rowId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static async Task<bool> ImportRowAsync(SqliteConnection conn, SqliteTransaction tx,
        long id, long gameId, string eventType, int gameTimeS, string details)
    {
        if (JsonNode.Parse(details) is not JsonObject d) return false;
        var type = eventType.Trim().ToUpperInvariant();
        var cid = (Guid.TryParse(EventJson.ReadString(d, "request_id"), out var parsed) ? parsed : LegacyCorrectionId(id)).ToString("D");

        string op;
        EventOriginal original;
        string subjectKey;
        var originalType = EventJson.ReadString(d, "original_type");
        if (!string.IsNullOrWhiteSpace(originalType))
        {
            op = CorrectionOps.Retype;
            var originalDetails = d["original_details"] switch
            {
                JsonObject o => o.ToJsonString(),
                JsonValue v when v.TryGetValue<string>(out var s) && EventJson.TryParseObject(s) is not null => s,
                _ => "{}",
            };
            original = new EventOriginal(originalType.Trim().ToUpperInvariant(),
                EventJson.ReadInt(d, "original_time_s") ?? gameTimeS, originalDetails);
            subjectKey = EventIdentity.KeyFor(original.ToEvent(gameId));
        }
        else
        {
            op = CorrectionOps.Add;
            original = EventOriginal.None;
            subjectKey = EventIdentity.UserKey(cid);
        }

        Dictionary<string, JsonNode?>? attrs = null;
        if (type == GameEvent.EventTypes.Trade)
        {
            var kind = EventJson.ReadString(d, "kind") ?? EventJson.ReadString(d, "classification") ?? "short";
            attrs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["kind"] = JsonValue.Create(kind.Trim().ToLowerInvariant()) };
        }
        var patch = new EventPatch(type, gameTimeS, EventJson.ReadInt(d, "end_s"), attrs);

        var reason = (EventJson.ReadString(d, "note") ?? "").Trim();
        if (reason.Length > EventCorrectionsRepository.ReasonMaxLength) reason = reason.Substring(0, EventCorrectionsRepository.ReasonMaxLength);
        var now = EventCorrectionSql.Now();

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR IGNORE INTO event_corrections (
                    correction_id, game_id, subject_key, subject_type, subject_time_s, op, patch, original, reason,
                    detector, detector_v, app_version, supersedes_id, rebased_from, delta_s, state, applied_event_id,
                    applied_at, apply_error, share_state, shared_at, created_at, updated_at)
                VALUES (
                    @cid, @g, @sk, @st, @sts, @op, @patch, @orig, @reason,
                    'reviewed_encounter', @detv, '', NULL, '', @delta, 'active', @applied,
                    @now, '', 'held', NULL, @now, @now)
                """;
            insert.Parameters.AddWithValue("@cid", cid);
            insert.Parameters.AddWithValue("@g", gameId);
            insert.Parameters.AddWithValue("@sk", subjectKey);
            insert.Parameters.AddWithValue("@st", op == CorrectionOps.Add ? type : original.EventType);
            insert.Parameters.AddWithValue("@sts", op == CorrectionOps.Add ? gameTimeS : original.GameTimeS);
            insert.Parameters.AddWithValue("@op", op);
            insert.Parameters.AddWithValue("@patch", patch.ToJson());
            insert.Parameters.AddWithValue("@orig", original.ToJson());
            insert.Parameters.AddWithValue("@reason", reason);
            insert.Parameters.AddWithValue("@detv", EventJson.ReadInt(d, "classification_version") ?? 1);
            insert.Parameters.AddWithValue("@delta", op == CorrectionOps.Retype ? gameTimeS - original.GameTimeS : DBNull.Value);
            insert.Parameters.AddWithValue("@applied", id);
            insert.Parameters.AddWithValue("@now", now);
            await insert.ExecuteNonQueryAsync();
        }

        d[EventPatching.MarkerKey] = new JsonObject
        {
            ["id"] = cid,
            ["op"] = op,
            ["attrs"] = new JsonArray(patch.AttrKeys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
        };
        using var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE game_events SET event_key = @k, details = @d
            WHERE id = @id AND json_extract(details, '$.correction') IS NULL
            """;
        update.Parameters.AddWithValue("@k", subjectKey);
        update.Parameters.AddWithValue("@d", d.ToJsonString());
        update.Parameters.AddWithValue("@id", id);
        return await update.ExecuteNonQueryAsync() > 0;
    }
}

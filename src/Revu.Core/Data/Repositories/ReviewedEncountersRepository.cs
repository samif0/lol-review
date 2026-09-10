using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>Explicit review judgments, never inferred from duration or HP loss.
/// v3.11: the legacy /api/encounter/save entry point. Validation and messages are unchanged;
/// the write itself is delegated to the corrections ledger (<see cref="EventCorrectionsRepository"/>)
/// so an encounter review is one more correction row, keyed and re-applied like every other.</summary>
public sealed class ReviewedEncountersRepository(IDbConnectionFactory factory)
{
    public const string Source = "reviewed_encounter";

    public static bool IsEncounter(string type) => type is
        GameEvent.EventTypes.Trade or GameEvent.EventTypes.AllIn or GameEvent.EventTypes.UncertainCombat;

    internal static JsonObject? ReviewDetails(string details)
    {
        try
        {
            var obj = JsonNode.Parse(details) as JsonObject;
            return obj?["source"]?.GetValue<string>() == Source ? obj : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    public async Task<int> SaveAsync(long gameId, int? eventId, string requestId,
        int startS, int endS, string classification, string? note)
    {
        if (gameId <= 0 || eventId is <= 0 || !Guid.TryParse(requestId, out _)
            || startS < 0 || endS < startS || endS > 86400 || (note?.Length ?? 0) > 2000)
            throw new ArgumentException("Invalid encounter, time range or note.");
        var type = classification switch
        {
            "short" or "extended" => GameEvent.EventTypes.Trade,
            "all_in" => GameEvent.EventTypes.AllIn,
            "uncertain" => GameEvent.EventTypes.UncertainCombat,
            _ => throw new ArgumentException("Choose short, extended, all_in or uncertain.")
        };

        // Validation reads its own connection and releases it BEFORE the ledger opens its
        // write transaction (a shared-cache in-memory DB would otherwise see a lock).
        GameEvent? row = null;
        using (var conn = factory.CreateConnection())
        {
            using var game = conn.CreateCommand();
            game.CommandText = "SELECT game_duration FROM games WHERE game_id = @game";
            game.Parameters.AddWithValue("@game", gameId);
            var duration = await game.ExecuteScalarAsync();
            if (duration is null || duration is DBNull)
                throw new ArgumentException("Game not found.");
            if (Convert.ToInt32(duration) > 0 && endS > Convert.ToInt32(duration))
                throw new ArgumentException("Encounter extends past the game duration.");

            // A retry of an add reuses the same request identity, including after edits.
            using var rows = conn.CreateCommand();
            rows.CommandText = "SELECT id, event_type, game_time_s, details, event_key FROM game_events WHERE game_id = @game";
            rows.Parameters.AddWithValue("@game", gameId);
            using var reader = await rows.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var details = reader.IsDBNull(3) ? "{}" : reader.GetString(3);
                if (eventId is null && ReviewDetails(details)?["request_id"]?.GetValue<string>() == requestId)
                    return id;
                if (id != eventId) continue;
                if (!IsEncounter(reader.GetString(1)))
                    throw new ArgumentException("Only trade and combat encounter markers can be corrected.");
                row = new GameEvent
                {
                    Id = id,
                    GameId = gameId,
                    EventType = reader.GetString(1),
                    GameTimeS = reader.GetInt32(2),
                    Details = details,
                    EventKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                };
            }
            if (eventId.HasValue && row is null)
                throw new ArgumentException("Encounter not found in this game.");
        }

        var ledger = new EventCorrectionsRepository(factory);
        var attrs = type == GameEvent.EventTypes.Trade
            ? new Dictionary<string, JsonNode?> { ["kind"] = JsonValue.Create(classification) }
            : null;
        var patch = new EventPatch(type, startS, endS, attrs);
        var trimmedNote = note?.Trim() ?? "";
        var reason = trimmedNote.Length > EventCorrectionsRepository.ReasonMaxLength
            ? trimmedNote.Substring(0, EventCorrectionsRepository.ReasonMaxLength)
            : trimmedNote;

        if (row is null)
        {
            var added = await ledger.SaveAsync(new EventCorrectionRequest(gameId, requestId, CorrectionOps.Add, null, patch,
                Reason: reason, EncounterNote: trimmedNote));
            return (int)(added.AppliedEventId ?? 0);
        }

        var op = !string.Equals(row.EventType, type, StringComparison.OrdinalIgnoreCase)
            ? CorrectionOps.Retype
            : row.GameTimeS != startS || EncounterEnd(row) != endS
                ? CorrectionOps.Retime
                : CorrectionOps.Attr;
        var subject = new EventCorrectionSubject(row.EventKey, row.Id, row.EventType, row.GameTimeS);
        if (op == CorrectionOps.Attr && (attrs is null || SameKind(row, classification)))
        {
            // Same class and same window. A detected marker the user has never reviewed is
            // being confirmed at its own window (legacy: the row became a reviewed encounter),
            // so record a confirm; an already reviewed one can only differ by its note, which
            // is rewritten in place (legacy: a silent rewrite, no ledger row).
            if (ReviewDetails(row.Details) is null)
            {
                var confirmed = await ledger.SaveAsync(new EventCorrectionRequest(gameId, requestId, CorrectionOps.Confirm, subject,
                    EventPatch.Empty, Reason: reason, EncounterNote: trimmedNote));
                return (int)(confirmed.AppliedEventId ?? row.Id);
            }
            await WriteNoteAsync(gameId, row, trimmedNote);
            return row.Id;
        }
        try
        {
            var saved = await ledger.SaveAsync(new EventCorrectionRequest(gameId, requestId, op, subject, patch,
                Reason: reason, EncounterNote: trimmedNote));
            return (int)(saved.AppliedEventId ?? row.Id);
        }
        catch (ArgumentException ex) when (ex.Message == EventCorrectionsRepository.NothingToChangeMessage)
        {
            await WriteNoteAsync(gameId, row, trimmedNote);
            return row.Id;
        }
    }

    private static bool SameKind(GameEvent row, string classification)
    {
        try
        {
            var kind = (JsonNode.Parse(row.Details) as JsonObject)?["kind"];
            return kind is JsonValue v && v.TryGetValue<string>(out var s)
                && string.Equals(s.Trim(), classification, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>A note-only edit on a reviewed encounter: details.note is rewritten in place and
    /// every other key (the marker included) is kept. The note is not a corrected attribute, so
    /// no ledger row is needed; a later correction carries it through details.note.</summary>
    private async Task WriteNoteAsync(long gameId, GameEvent row, string trimmedNote)
    {
        JsonObject details;
        try { details = JsonNode.Parse(row.Details) as JsonObject ?? new JsonObject(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { details = new JsonObject(); }
        var stored = details["note"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        if (string.Equals(stored, trimmedNote, StringComparison.Ordinal)) return;
        details["note"] = trimmedNote;
        using var conn = factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE game_events SET details = @d WHERE id = @id AND game_id = @g";
        cmd.Parameters.AddWithValue("@d", details.ToJsonString());
        cmd.Parameters.AddWithValue("@id", row.Id);
        cmd.Parameters.AddWithValue("@g", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static int EncounterEnd(GameEvent row)
    {
        try
        {
            var obj = JsonNode.Parse(row.Details) as JsonObject;
            return obj?["end_s"] is JsonValue v && v.TryGetValue<int>(out var end) ? end : row.GameTimeS;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return row.GameTimeS; }
    }
}

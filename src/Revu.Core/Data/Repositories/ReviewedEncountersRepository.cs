using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>Explicit review judgments, never inferred from duration or HP loss.</summary>
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
        using var conn = factory.CreateConnection();
        using var tx = conn.BeginTransaction();
        using var game = conn.CreateCommand();
        game.Transaction = tx;
        game.CommandText = "SELECT game_duration FROM games WHERE game_id = @game";
        game.Parameters.AddWithValue("@game", gameId);
        var duration = await game.ExecuteScalarAsync();
        if (duration is null || duration is DBNull)
            throw new ArgumentException("Game not found.");
        if (Convert.ToInt32(duration) > 0 && endS > Convert.ToInt32(duration))
            throw new ArgumentException("Encounter extends past the game duration.");

        // A retry of an add reuses the same request identity, including after edits.
        using var rows = conn.CreateCommand();
        rows.Transaction = tx;
        rows.CommandText = "SELECT id, event_type, game_time_s, details FROM game_events WHERE game_id = @game";
        rows.Parameters.AddWithValue("@game", gameId);
        int? existingId = null;
        JsonObject? original = null;
        using (var reader = await rows.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var details = ReviewDetails(reader.GetString(3));
                if (eventId is null && details?["request_id"]?.GetValue<string>() == requestId)
                    return id;
                if (id != eventId) continue;
                if (!IsEncounter(reader.GetString(1)))
                    throw new ArgumentException("Only trade and combat encounter markers can be corrected.");
                existingId = id;
                original = details ?? new JsonObject
                {
                    ["original_type"] = reader.GetString(1),
                    ["original_time_s"] = reader.GetInt32(2),
                    ["original_details"] = reader.GetString(3)
                };
            }
        }
        if (eventId.HasValue && existingId is null)
            throw new ArgumentException("Encounter not found in this game.");
        var data = original ?? new JsonObject();
        data["source"] = Source;
        data["request_id"] ??= requestId;
        data["reviewed"] = true;
        data["classification_version"] = 1;
        data["classification"] = classification;
        data["kind"] = type == GameEvent.EventTypes.Trade ? classification : null;
        data["start_s"] = startS;
        data["end_s"] = endS;
        data["duration_s"] = endS - startS;
        data["note"] = note?.Trim() ?? "";
        using var save = conn.CreateCommand();
        save.Transaction = tx;
        save.CommandText = existingId.HasValue
            ? "UPDATE game_events SET event_type=@type, game_time_s=@start, details=@details WHERE id=@id AND game_id=@game RETURNING id"
            : "INSERT INTO game_events(game_id,event_type,game_time_s,details) VALUES(@game,@type,@start,@details) RETURNING id";
        save.Parameters.AddWithValue("@id", (object?)existingId ?? DBNull.Value);
        save.Parameters.AddWithValue("@game", gameId);
        save.Parameters.AddWithValue("@type", type);
        save.Parameters.AddWithValue("@start", startS);
        save.Parameters.AddWithValue("@details", data.ToJsonString());
        var savedId = Convert.ToInt32(await save.ExecuteScalarAsync());
        await tx.CommitAsync();
        return savedId;
    }
}

#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Data.Repositories;

/// <summary>Rule C (attribute re-merge after a details stamp) and the second half of rule G
/// (orphan resolution) plus the row helpers both halves of the applier share.</summary>
public static partial class EventCorrectionApplier
{
    /// <summary>Rule C. Call AFTER the UPDATE of details, inside the caller's transaction.
    /// Re-merges patch.attrs (and the marker) of applicable corrections with applied_event_id == eventId;
    /// flips to absorbed when the detector wrote every corrected attribute with the corrected value,
    /// back to active when it wrote a different one, and leaves the state alone when it wrote none
    /// of them (retime/retype/confirm rows, or a key this detector never decides).</summary>
    public static async Task ReapplyAttrsAsync(SqliteConnection conn, SqliteTransaction tx, int eventId)
    {
        List<EventCorrection> corrections;
        GameEvent? fresh;
        try
        {
            corrections = await EventCorrectionSql.LoadByAppliedEventAsync(conn, tx, eventId);
            if (corrections.Count == 0) return;
            fresh = await LoadRowAsync(conn, tx, eventId);
        }
        catch (SqliteException ex)
        {
            CoreDiagnostics.WriteVerbose($"Event corrections: rule C skipped for event {eventId}: {ex.Message}");
            return;
        }
        if (fresh is null) return;

        foreach (var c in corrections)
        {
            if (c.Op == CorrectionOps.Remove) continue;
            try
            {
                // Only the attribute half of a fix can be confirmed or contradicted by a details
                // stamp; the row's type and time are already the corrected ones, so a retime,
                // retype or confirm keeps whatever state rule A or G awarded. For attrs the
                // verdict comes from what the stamp actually wrote (the map-state pass strips
                // the corrected keys before it decides): a key it never writes is silence, not
                // agreement, so a jungle_gank fix is untouched by a map-state re-run and a
                // fog_death=true fix the detector still misses stays active.
                var agrees = StampAgrees(fresh, c.Patch);
                var note = EventJson.ReadString(EventJson.TryParseObject(fresh.Details), "note");
                EventPatching.Apply(fresh, c.Patch, c.Original, c.CorrectionId, c.Op, c.SubjectKey, c.Reason, note);
                await UpdateRowAsync(conn, tx, fresh);
                var state = agrees switch
                {
                    true => CorrectionStates.Absorbed,
                    false => c.State == CorrectionStates.Absorbed ? CorrectionStates.Active : c.State,
                    null => c.State,
                };
                await SetStateAsync(conn, tx, c, state, c.AppliedEventId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CoreDiagnostics.WriteVerbose($"Event corrections: correction {c.CorrectionId} failed to re-merge: {ex.Message}");
                await RecordErrorAsync(conn, tx, c, ex);
            }
        }
    }

    /// <summary>Rule G, second half. For applicable non-remove corrections whose applied row is gone
    /// (applied_event_id NULL or not found), match against the game's unmarked rows (FindMatch), apply
    /// the cumulative patch to the matched row in place, set applied_event_id and state (active, or absorbed
    /// when Satisfies), rebase on a fuzzy hit; remove corrections whose twin exists delete it. Never inserts.
    /// Returns the number resolved.</summary>
    public static async Task<int> ResolveOrphansAsync(SqliteConnection conn, SqliteTransaction tx, long gameId)
    {
        List<EventCorrection> corrections;
        List<GameEvent> rows;
        try
        {
            corrections = await EventCorrectionSql.LoadApplicableAsync(conn, tx, gameId);
            if (corrections.Count == 0) return 0;
            rows = await EventCorrectionSql.LoadRowsAsync(conn, tx, gameId);
        }
        catch (SqliteException ex)
        {
            CoreDiagnostics.WriteVerbose($"Event corrections: orphan resolution skipped for game {gameId}: {ex.Message}");
            return 0;
        }

        var batchKeys = EventIdentity.KeyForBatch(rows);
        var keys = new string[rows.Count];
        var indexById = new Dictionary<long, int>();
        for (var i = 0; i < rows.Count; i++)
        {
            keys[i] = rows[i].EventKey ?? batchKeys[i];
            indexById.TryAdd(rows[i].Id, i);
        }

        // Rows still bound to an applicable correction are never candidates for another one.
        var claimed = new HashSet<int>();
        foreach (var c in corrections)
            if (c.AppliedEventId is { } bound && indexById.TryGetValue(bound, out var index)) claimed.Add(index);

        var resolved = 0;
        foreach (var c in corrections)
        {
            try
            {
                if (c.Op == CorrectionOps.Remove)
                {
                    var twin = EventIdentity.FindMatch(c, rows, keys, claimed);
                    if (twin is null) continue;
                    claimed.Add(twin.Index);
                    var row = rows[twin.Index];
                    await ExecAsync(conn, tx, "DELETE FROM game_events WHERE id = @id", ("@id", row.Id));
                    if (IsDeath(row.EventType))
                        await ExecAsync(conn, tx, "DELETE FROM death_classifications WHERE game_id = @g AND game_time_s = @t",
                            ("@g", gameId), ("@t", row.GameTimeS));
                    if (!twin.Exact && !string.Equals(twin.CandidateKey, c.SubjectKey, StringComparison.Ordinal))
                        await EventCorrectionSql.RebaseSubjectAsync(conn, tx, c.Id, twin.CandidateKey, c.SubjectKey);
                    await SetStateAsync(conn, tx, c, CorrectionStates.Active, null);
                    resolved++;
                    continue;
                }

                if (c.AppliedEventId is { } applied && indexById.ContainsKey(applied)) continue;

                var match = EventIdentity.FindMatch(c, rows, keys, claimed);
                if (match is null) continue;
                claimed.Add(match.Index);
                var target = rows[match.Index];
                var key = c.Op == CorrectionOps.Add ? c.SubjectKey : match.CandidateKey;
                var satisfied = EventIdentity.Satisfies(target, c.Patch, c.Original);
                EventPatching.Apply(target, c.Patch, c.Original, c.CorrectionId, c.Op, key, c.Reason);
                await UpdateRowAsync(conn, tx, target);
                if (c.Op != CorrectionOps.Add && !match.Exact && !string.Equals(key, c.SubjectKey, StringComparison.Ordinal))
                    await EventCorrectionSql.RebaseSubjectAsync(conn, tx, c.Id, key, c.SubjectKey);
                await SetStateAsync(conn, tx, c, satisfied ? CorrectionStates.Absorbed : CorrectionStates.Active, target.Id);
                resolved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CoreDiagnostics.WriteVerbose($"Event corrections: correction {c.CorrectionId} failed to resolve: {ex.Message}");
                await RecordErrorAsync(conn, tx, c, ex);
            }
        }
        return resolved;
    }

    // -- shared helpers -----------------------------------------------------------------------

    /// <summary>Rule C verdict over the keys of <paramref name="patch"/>.Attrs as the detector left
    /// them: true when every corrected key it wrote agrees (after <see cref="EventIdentity.Normalize"/>)
    /// and it wrote at least one; false when any written key disagrees; null when the patch has no
    /// attrs or the stamp wrote none of them (silence).</summary>
    private static bool? StampAgrees(GameEvent fresh, EventPatch patch)
    {
        if (patch.Attrs is not { Count: > 0 }) return null;
        var details = EventJson.TryParseObject(fresh.Details);
        if (details is null) return null;
        var written = false;
        foreach (var pair in patch.Attrs)
        {
            if (!details.ContainsKey(pair.Key)) continue;
            written = true;
            if (!string.Equals(EventIdentity.Normalize(details[pair.Key]), EventIdentity.Normalize(pair.Value), StringComparison.Ordinal))
                return false;
        }
        return written ? true : null;
    }

    /// <summary>Writes the state only when something changes (state, applied id, or a stale apply_error
    /// to clear), so an untouched correction keeps its updated_at.</summary>
    private static Task SetStateAsync(SqliteConnection conn, SqliteTransaction tx, EventCorrection c, string state, long? appliedEventId) =>
        state != c.State || appliedEventId != c.AppliedEventId || c.ApplyError.Length > 0
            ? EventCorrectionSql.SetStateAsync(conn, tx, c.Id, state, appliedEventId)
            : Task.CompletedTask;

    /// <summary>A per-correction failure is recorded on its row (state unchanged) and never rethrown.</summary>
    private static async Task RecordErrorAsync(SqliteConnection conn, SqliteTransaction tx, EventCorrection c, Exception ex)
    {
        try
        {
            var message = ex.Message.Length > 500 ? ex.Message.Substring(0, 500) : ex.Message;
            await EventCorrectionSql.SetStateAsync(conn, tx, c.Id, c.State, c.AppliedEventId, message);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            CoreDiagnostics.WriteVerbose($"Event corrections: could not record apply_error for {c.CorrectionId}: {inner.Message}");
        }
    }

    private static async Task<GameEvent?> LoadRowAsync(SqliteConnection conn, SqliteTransaction tx, long eventId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, game_id, event_type, game_time_s, details, event_key
            FROM game_events WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", eventId);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? EventCorrectionSql.ReadRow(reader) : null;
    }

    private static Task UpdateRowAsync(SqliteConnection conn, SqliteTransaction tx, GameEvent e) =>
        ExecAsync(conn, tx, "UPDATE game_events SET event_type = @t, game_time_s = @s, details = @d, event_key = @k WHERE id = @id",
            ("@t", e.EventType), ("@s", e.GameTimeS), ("@d", e.Details), ("@k", (object?)e.EventKey ?? DBNull.Value), ("@id", e.Id));

    private static async Task ExecAsync(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static bool IsDeath(string? type) => string.Equals(type, GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase);

    private static GameEvent Clone(GameEvent e) => new()
    {
        Id = e.Id, GameId = e.GameId, EventType = e.EventType, GameTimeS = e.GameTimeS, Details = e.Details, EventKey = e.EventKey,
    };
}

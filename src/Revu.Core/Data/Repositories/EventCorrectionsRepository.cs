#nullable enable

using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// Rules D and E of the corrections ledger: validate a fix, apply it to the game_events row in
/// place (moving the death classification with a retimed DEATH), record the cumulative patch
/// as one ledger row, and undo it from the original snapshot. One connection and one
/// transaction per call; the queries live in the partial next door.
/// </summary>
public sealed partial class EventCorrectionsRepository : IEventCorrectionsRepository
{
    private readonly IDbConnectionFactory _factory;

    public EventCorrectionsRepository(IDbConnectionFactory factory) => _factory = factory;

    public const int ReasonMaxLength = 280;
    public const int EncounterNoteMaxLength = 2000;

    public const string GameMissingMessage = "That game is not on record.";
    public const string OpMessage = "Pick a correction type: retype, retime, attr, remove, add or confirm.";
    public const string CorrectionIdMessage = "A correction id is required.";
    public const string EventMissingMessage = "That event is not in this game.";
    public const string EventChangedMessage = "That event changed since the page loaded. Reload and try again.";
    public const string EventRemovedMessage = "This event was removed. Revert the removal first.";
    public const string SyntheticFightMessage = "A synthetic fight cannot be corrected until the post-game pass stores it.";
    public const string TypeMessage = "Pick an event type from the list.";
    public const string TimeMessage = "Enter a time inside this game.";
    public const string ReasonMessage = "Keep the reason under 280 characters.";
    public const string NothingToChangeMessage = "Nothing to change.";
    public const string CorrectionMissingMessage = "That correction is not on record.";
    public const string CorrectionReplacedMessage = "That correction was already replaced; revert the newer one.";
    public const string CauseResetMessage = "Cause reset: the death at the new time already had a cause.";

    public async Task<EventCorrectionSaveResult> SaveAsync(EventCorrectionRequest request)
    {
        // 1. Validate before touching the DB.
        if (request is null || request.GameId <= 0) throw new ArgumentException(GameMissingMessage);
        if (!Guid.TryParse(request.CorrectionId, out var cidGuid)) throw new ArgumentException(CorrectionIdMessage);
        var cid = cidGuid.ToString("D");
        var op = (request.Op ?? "").Trim().ToLowerInvariant();
        if (!CorrectionOps.IsSaveable(op)) throw new ArgumentException(OpMessage);
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length > ReasonMaxLength) throw new ArgumentException(ReasonMessage);
        var note = request.EncounterNote?.Trim();
        if (note is { Length: > EncounterNoteMaxLength }) note = note.Substring(0, EncounterNoteMaxLength);

        var patch = request.Patch ?? EventPatch.Empty;
        var subject = request.Subject;
        if (op == CorrectionOps.Add)
        {
            if (!EventCorrectionCatalog.IsKnownType(patch.EventType)) throw new ArgumentException(TypeMessage);
            if (patch.GameTimeS is null) throw new ArgumentException(TimeMessage);
        }
        else
        {
            if (subject is null || string.IsNullOrWhiteSpace(subject.Type)) throw new ArgumentException(EventMissingMessage);
            if (subject.EventId is < 0) throw new ArgumentException(SyntheticFightMessage);
            if (subject.TimeS < 0) throw new ArgumentException(EventMissingMessage);
        }
        if (patch.EventType is not null && !EventCorrectionCatalog.IsKnownType(patch.EventType)) throw new ArgumentException(TypeMessage);
        if (patch.GameTimeS is < 0 || patch.EndS is < 0) throw new ArgumentException(TimeMessage);
        if (patch.GameTimeS is { } ps && patch.EndS is { } pe && pe < ps) throw new ArgumentException(TimeMessage);
        var effectiveType = (patch.EventType ?? subject?.Type ?? "").Trim().ToUpperInvariant();
        if (patch.Attrs is { Count: > 0 })
        {
            var attrError = EventCorrectionCatalog.ValidateAttrs(effectiveType, patch.Attrs);
            if (attrError is not null) throw new ArgumentException(attrError);
        }
        if (op == CorrectionOps.Attr && (patch.Attrs is null || patch.Attrs.Count == 0)) throw new ArgumentException(NothingToChangeMessage);
        if (op == CorrectionOps.Retype && patch.EventType is null) throw new ArgumentException(TypeMessage);
        if (op == CorrectionOps.Retime && patch.GameTimeS is null && patch.EndS is null) throw new ArgumentException(NothingToChangeMessage);
        patch = NormalizePatch(patch, effectiveType);

        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        // 2. The game and its duration.
        var duration = await GameDurationAsync(conn, tx, request.GameId);
        if (duration is null) throw new ArgumentException(GameMissingMessage);
        if (duration > 0 && (patch.GameTimeS > duration || patch.EndS > duration)) throw new ArgumentException(TimeMessage);

        // 3. Idempotency on the correction id, in any state.
        var existing = await EventCorrectionSql.FindByCorrectionIdAsync(conn, tx, cid);
        if (existing is not null)
            return new EventCorrectionSaveResult(existing.Id, cid, existing.Op, existing.State, existing.SubjectKey,
                existing.AppliedEventId, Idempotent: true, "");

        // 4. Subject resolution.
        GameEvent? row = null;
        string subjectKey;
        if (op == CorrectionOps.Add)
        {
            subjectKey = EventIdentity.UserKey(cid);
        }
        else
        {
            row = await ResolveSubjectAsync(conn, tx, request.GameId, subject!);
            if (row.EventKey is null)
            {
                var all = await EventCorrectionSql.LoadRowsAsync(conn, tx, request.GameId);
                var keys = EventIdentity.KeyForBatch(all);
                var index = all.FindIndex(r => r.Id == row.Id);
                row.EventKey = index >= 0 ? keys[index] : EventIdentity.KeyFor(row);
                await EventCorrectionSql.SetEventKeyAsync(conn, tx, row.Id, row.EventKey);
            }
            subjectKey = row.EventKey;
        }

        // 5. The prior applicable row on the same subject.
        var prior = await EventCorrectionSql.FindActiveBySubjectAsync(conn, tx, request.GameId, subjectKey);
        if (prior is not null && prior.Op == CorrectionOps.Remove && op != CorrectionOps.Add)
            throw new ArgumentException(EventRemovedMessage);

        // 6. Original snapshot and the cumulative patch.
        var original = op == CorrectionOps.Add ? EventOriginal.None : prior?.Original ?? EventPatching.Snapshot(row!);
        var cumulative = op switch
        {
            CorrectionOps.Remove => EventPatch.Empty,
            CorrectionOps.Confirm => (prior?.Patch ?? EventPatch.Empty) with { Confirmed = true },
            _ => (prior?.Patch ?? EventPatch.Empty).Overlay(patch),
        };

        // 7. A fix that changes nothing is rejected, not recorded.
        if (op is CorrectionOps.Retype or CorrectionOps.Retime or CorrectionOps.Attr)
        {
            var preview = Clone(row!);
            EventPatching.Apply(preview, cumulative, original, cid, op, subjectKey, reason, note);
            if (EffectiveTuple(row!, cumulative) == EffectiveTuple(preview, cumulative))
                throw new ArgumentException(NothingToChangeMessage);
        }

        // 8. Apply in place.
        long? appliedEventId = null;
        var message = "";
        switch (op)
        {
            case CorrectionOps.Add:
            {
                var e = new GameEvent
                {
                    GameId = request.GameId,
                    EventType = cumulative.EventType ?? effectiveType,
                    GameTimeS = cumulative.GameTimeS ?? 0,
                    Details = "{}",
                };
                EventPatching.Apply(e, cumulative, EventOriginal.None, cid, op, subjectKey, reason, note);
                appliedEventId = await InsertRowAsync(conn, tx, e);
                break;
            }
            case CorrectionOps.Remove:
            {
                await ExecAsync(conn, tx, "DELETE FROM game_events WHERE id = @id", ("@id", row!.Id));
                if (IsDeath(row.EventType))
                    await ExecAsync(conn, tx, "DELETE FROM death_classifications WHERE game_id = @g AND game_time_s = @t",
                        ("@g", request.GameId), ("@t", row.GameTimeS));
                break;
            }
            default:
            {
                var e = Clone(row!);
                var tOld = row!.GameTimeS;
                var typeOld = row.EventType;
                EventPatching.Apply(e, cumulative, original, cid, op, subjectKey, reason, note);
                await UpdateRowAsync(conn, tx, e);
                appliedEventId = e.Id;
                if ((IsDeath(original.EventType) || IsDeath(e.EventType) || IsDeath(typeOld)) && e.GameTimeS != tOld
                    && await MoveDeathClassificationAsync(conn, tx, request.GameId, tOld, e.GameTimeS))
                    message = CauseResetMessage;
                break;
            }
        }

        // 9. Ledger.
        if (prior is not null)
            await EventCorrectionSql.SetStateAsync(conn, tx, prior.Id, CorrectionStates.Superseded, prior.AppliedEventId);
        var now = EventCorrectionSql.Now();
        var (detector, detectorVersion) = EventPatching.DetectorOf(op, original, cumulative);
        var subjectType = original.IsNone ? prior?.SubjectType ?? (cumulative.EventType ?? effectiveType) : original.EventType;
        var subjectTimeS = original.IsNone ? prior?.SubjectTimeS ?? (cumulative.GameTimeS ?? 0) : original.GameTimeS;
        var id = await EventCorrectionSql.InsertAsync(conn, tx, new EventCorrection(
            Id: 0,
            CorrectionId: cid,
            GameId: request.GameId,
            SubjectKey: subjectKey,
            SubjectType: subjectType,
            SubjectTimeS: subjectTimeS,
            Op: op,
            Patch: cumulative,
            Original: original,
            Reason: reason,
            Detector: detector,
            DetectorVersion: detectorVersion,
            AppVersion: request.AppVersion ?? "",
            SupersedesId: prior?.Id,
            RebasedFrom: "",
            DeltaS: op != CorrectionOps.Add && cumulative.GameTimeS is { } newTime && !original.IsNone ? newTime - original.GameTimeS : null,
            State: CorrectionStates.Active,
            AppliedEventId: appliedEventId,
            AppliedAt: now,
            ApplyError: "",
            ShareState: CorrectionShareStates.Held,
            SharedAt: null,
            CreatedAt: now,
            UpdatedAt: now));

        // 10. Commit.
        await tx.CommitAsync();
        return new EventCorrectionSaveResult(id, cid, op, CorrectionStates.Active, subjectKey, appliedEventId, Idempotent: false, message);
    }

    public async Task<EventCorrectionSaveResult> RevertAsync(long gameId, string correctionId, string reason = "", string appVersion = "")
    {
        if (!Guid.TryParse(correctionId, out var cidGuid)) throw new ArgumentException(CorrectionMissingMessage);
        var cid = cidGuid.ToString("D");
        reason = (reason ?? "").Trim();
        if (reason.Length > ReasonMaxLength) reason = reason.Substring(0, ReasonMaxLength);

        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var target = await EventCorrectionSql.FindByCorrectionIdAsync(conn, tx, cid);
        if (target is null || target.GameId != gameId || target.Op == CorrectionOps.Revert)
            throw new ArgumentException(CorrectionMissingMessage);
        if (target.State == CorrectionStates.Reverted)
            return new EventCorrectionSaveResult(target.Id, cid, target.Op, target.State, target.SubjectKey,
                target.AppliedEventId, Idempotent: true, "");
        if (target.State == CorrectionStates.Superseded) throw new ArgumentException(CorrectionReplacedMessage);

        long? restoredId = null;
        var applied = await FindAppliedRowAsync(conn, tx, target);
        if (target.Op == CorrectionOps.Remove)
        {
            if (target.Original.IsNone)
            {
                // The removed event was one the user added, so there is no detected original to
                // bring back: rebuild it from the fix this remove superseded (its cumulative patch
                // is the row as it stood) and hand that fix its undo back, so the restored marker's
                // correction id can be reverted or edited again.
                var superseded = target.SupersedesId is { } sid ? await EventCorrectionSql.FindByIdAsync(conn, tx, sid) : null;
                if (superseded is not null && superseded.Op != CorrectionOps.Revert && superseded.Op != CorrectionOps.Remove)
                {
                    var e = new GameEvent
                    {
                        GameId = gameId,
                        EventType = superseded.Patch.EventType ?? superseded.SubjectType,
                        GameTimeS = superseded.Patch.GameTimeS ?? superseded.SubjectTimeS,
                        Details = "{}",
                    };
                    EventPatching.Apply(e, superseded.Patch, EventOriginal.None, superseded.CorrectionId, superseded.Op,
                        target.SubjectKey, superseded.Reason);
                    restoredId = await InsertRowAsync(conn, tx, e);
                    await EventCorrectionSql.SetStateAsync(conn, tx, superseded.Id, CorrectionStates.Active, restoredId);
                }
            }
            else
            {
                var e = target.Original.ToEvent(gameId);
                e.EventKey = target.SubjectKey;
                restoredId = await InsertRowAsync(conn, tx, e);
            }
        }
        else if (target.Op == CorrectionOps.Add || target.Original.IsNone)
        {
            // The event never existed as detected: undoing the fix is removing the row.
            if (applied is not null)
            {
                await ExecAsync(conn, tx, "DELETE FROM game_events WHERE id = @id", ("@id", applied.Id));
                if (IsDeath(applied.EventType))
                    await ExecAsync(conn, tx, "DELETE FROM death_classifications WHERE game_id = @g AND game_time_s = @t",
                        ("@g", gameId), ("@t", applied.GameTimeS));
            }
        }
        else if (applied is not null)
        {
            var tOld = applied.GameTimeS;
            var typeOld = applied.EventType;
            EventPatching.Restore(applied, target.Original);
            await UpdateRowAsync(conn, tx, applied);
            restoredId = applied.Id;
            if ((IsDeath(typeOld) || IsDeath(applied.EventType)) && applied.GameTimeS != tOld)
                await MoveDeathClassificationAsync(conn, tx, gameId, tOld, applied.GameTimeS);
        }

        await EventCorrectionSql.SetStateAsync(conn, tx, target.Id, CorrectionStates.Reverted, restoredId);
        var now = EventCorrectionSql.Now();
        var revertId = await EventCorrectionSql.InsertAsync(conn, tx, target with
        {
            Id = 0,
            CorrectionId = Guid.NewGuid().ToString("D"),
            Op = CorrectionOps.Revert,
            Patch = EventPatch.Empty,
            Reason = reason,
            AppVersion = appVersion ?? "",
            SupersedesId = target.Id,
            RebasedFrom = "",
            DeltaS = null,
            State = CorrectionStates.Reverted,
            AppliedEventId = restoredId,
            AppliedAt = now,
            ApplyError = "",
            ShareState = CorrectionShareStates.Held,
            SharedAt = null,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await tx.CommitAsync();
        return new EventCorrectionSaveResult(revertId, target.CorrectionId, CorrectionOps.Revert, CorrectionStates.Reverted,
            target.SubjectKey, restoredId, Idempotent: false, "");
    }

    // -- subject and row helpers ----------------------------------------------------------

    private static async Task<GameEvent> ResolveSubjectAsync(SqliteConnection conn, SqliteTransaction tx, long gameId, EventCorrectionSubject subject)
    {
        List<GameEvent> found = [];
        if (!string.IsNullOrWhiteSpace(subject.EventKey))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, game_id, event_type, game_time_s, details, event_key
                FROM game_events WHERE game_id = @g AND event_key = @k ORDER BY id ASC
                """;
            cmd.Parameters.AddWithValue("@g", gameId);
            cmd.Parameters.AddWithValue("@k", subject.EventKey);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) found.Add(EventCorrectionSql.ReadRow(reader));
        }
        if (found.Count == 0 && subject.EventId is > 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, game_id, event_type, game_time_s, details, event_key
                FROM game_events WHERE id = @id AND game_id = @g
                """;
            cmd.Parameters.AddWithValue("@id", subject.EventId.Value);
            cmd.Parameters.AddWithValue("@g", gameId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) found.Add(EventCorrectionSql.ReadRow(reader));
        }
        if (found.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(subject.EventKey))
            {
                var removed = await EventCorrectionSql.FindActiveBySubjectAsync(conn, tx, gameId, subject.EventKey);
                if (removed is { Op: CorrectionOps.Remove }) throw new ArgumentException(EventRemovedMessage);
            }
            throw new ArgumentException(EventMissingMessage);
        }
        var row = found.FirstOrDefault(r => Matches(r, subject)) ?? found[0];
        if (!Matches(row, subject)) throw new ArgumentException(EventChangedMessage);
        return row;
    }

    private static bool Matches(GameEvent row, EventCorrectionSubject subject) =>
        string.Equals(row.EventType, subject.Type, StringComparison.OrdinalIgnoreCase) && row.GameTimeS == subject.TimeS;

    private static async Task<GameEvent?> FindAppliedRowAsync(SqliteConnection conn, SqliteTransaction tx, EventCorrection target)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT id, game_id, event_type, game_time_s, details, event_key
            FROM game_events
            WHERE game_id = @g AND ((@id IS NOT NULL AND id = @id) OR (event_key = @k AND {EventCorrectionSql.IsCorrectedPredicate}))
            ORDER BY CASE WHEN id = @id THEN 0 ELSE 1 END, id ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@g", target.GameId);
        cmd.Parameters.AddWithValue("@id", (object?)target.AppliedEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@k", target.SubjectKey);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? EventCorrectionSql.ReadRow(reader) : null;
    }

    private static async Task<long> InsertRowAsync(SqliteConnection conn, SqliteTransaction tx, GameEvent e)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO game_events (game_id, event_type, game_time_s, details, event_key)
            VALUES (@g, @t, @s, @d, @k) RETURNING id
            """;
        cmd.Parameters.AddWithValue("@g", e.GameId);
        cmd.Parameters.AddWithValue("@t", e.EventType);
        cmd.Parameters.AddWithValue("@s", e.GameTimeS);
        cmd.Parameters.AddWithValue("@d", string.IsNullOrWhiteSpace(e.Details) ? "{}" : e.Details);
        cmd.Parameters.AddWithValue("@k", (object?)e.EventKey ?? DBNull.Value);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        e.Id = (int)id;
        return id;
    }

    private static Task UpdateRowAsync(SqliteConnection conn, SqliteTransaction tx, GameEvent e) =>
        ExecAsync(conn, tx, "UPDATE game_events SET event_type = @t, game_time_s = @s, details = @d, event_key = @k WHERE id = @id",
            ("@t", e.EventType), ("@s", e.GameTimeS), ("@d", e.Details), ("@k", (object?)e.EventKey ?? DBNull.Value), ("@id", e.Id));

    /// <summary>Rule D death-cause move. Returns true when the row at the new second already had a
    /// cause (the existing one wins and the moved cause is dropped).</summary>
    private static async Task<bool> MoveDeathClassificationAsync(SqliteConnection conn, SqliteTransaction tx, long gameId, int tOld, int tNew)
    {
        if (tOld == tNew) return false;
        if (!await HasClassificationAsync(conn, tx, gameId, tOld)) return false;
        if (await HasClassificationAsync(conn, tx, gameId, tNew))
        {
            await ExecAsync(conn, tx, "DELETE FROM death_classifications WHERE game_id = @g AND game_time_s = @t",
                ("@g", gameId), ("@t", tOld));
            return true;
        }
        await ExecAsync(conn, tx, "UPDATE death_classifications SET game_time_s = @new WHERE game_id = @g AND game_time_s = @old",
            ("@new", tNew), ("@g", gameId), ("@old", tOld));
        return false;
    }

    private static async Task<bool> HasClassificationAsync(SqliteConnection conn, SqliteTransaction tx, long gameId, int t)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM death_classifications WHERE game_id = @g AND game_time_s = @t LIMIT 1";
        cmd.Parameters.AddWithValue("@g", gameId);
        cmd.Parameters.AddWithValue("@t", t);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null && result is not DBNull;
    }

    private static async Task<int?> GameDurationAsync(SqliteConnection conn, SqliteTransaction tx, long gameId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT game_duration FROM games WHERE game_id = @g";
        cmd.Parameters.AddWithValue("@g", gameId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
    }

    private static async Task ExecAsync(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Choice attrs are stored lower-case so the tie resolver's string reads agree.</summary>
    private static EventPatch NormalizePatch(EventPatch patch, string type)
    {
        if (patch.Attrs is null || patch.Attrs.Count == 0)
            return patch with { EventType = patch.EventType?.Trim().ToUpperInvariant() };
        var def = EventCorrectionCatalog.Find(type);
        var attrs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var pair in patch.Attrs)
        {
            var attr = def?.Attrs.FirstOrDefault(a => a.Key == pair.Key);
            attrs[pair.Key] = attr?.Input == EventCorrectionCatalog.InputChoice && pair.Value is JsonValue v && v.TryGetValue<string>(out var s)
                ? JsonValue.Create(s.Trim().ToLowerInvariant())
                : pair.Value?.DeepClone();
        }
        return new EventPatch(patch.EventType?.Trim().ToUpperInvariant(), patch.GameTimeS, patch.EndS, attrs, patch.Confirmed);
    }

    /// <summary>(type, anchor, end_s, corrected attrs) of a row, the part of it a fix can change.</summary>
    private static string EffectiveTuple(GameEvent row, EventPatch cumulative)
    {
        var details = EventJson.TryParseObject(row.Details);
        var parts = new List<string>
        {
            row.EventType.ToUpperInvariant(),
            EventIdentity.AnchorOf(row).ToString(),
            EventJson.ReadInt(details, "end_s")?.ToString() ?? "",
        };
        foreach (var key in cumulative.AttrKeys)
            parts.Add(key + "=" + EventIdentity.Normalize(details?[key]));
        return string.Join("|", parts);
    }

    private static bool IsDeath(string? type) => string.Equals(type, GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase);

    private static GameEvent Clone(GameEvent e) => new()
    {
        Id = e.Id, GameId = e.GameId, EventType = e.EventType, GameTimeS = e.GameTimeS, Details = e.Details, EventKey = e.EventKey,
    };
}

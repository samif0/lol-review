#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// Ledger row access on an OPEN connection, shared by the ledger repository, the applier and the
/// legacy import. Never opens a connection and never commits: the caller owns both.
/// </summary>
public static class EventCorrectionSql
{
    /// <summary>The v16 delete/keep predicates (game_events). Both stay forever.</summary>
    public const string NotCorrectedPredicate =
        "(CASE WHEN json_valid(details) THEN json_extract(details, '$.correction') END) IS NULL";
    public const string NotReviewedPredicate =
        "(CASE WHEN json_valid(details) THEN json_extract(details, '$.source') END) IS NOT 'reviewed_encounter'";
    public const string IsCorrectedPredicate =
        "(CASE WHEN json_valid(details) THEN json_extract(details, '$.correction') END) IS NOT NULL";

    public const string SelectColumns =
        "id, correction_id, game_id, subject_key, subject_type, subject_time_s, op, patch, original, reason, " +
        "detector, detector_v, app_version, supersedes_id, rebased_from, delta_s, state, applied_event_id, " +
        "applied_at, apply_error, share_state, shared_at, created_at, updated_at";

    private const string ApplicablePredicate =
        "op <> 'revert' AND state IN ('active', 'absorbed', 'orphaned')";

    /// <summary>Maps one row in <see cref="SelectColumns"/> order.</summary>
    public static EventCorrection Read(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        CorrectionId: Str(r, 1),
        GameId: r.GetInt64(2),
        SubjectKey: Str(r, 3),
        SubjectType: Str(r, 4),
        SubjectTimeS: r.IsDBNull(5) ? 0 : r.GetInt32(5),
        Op: Str(r, 6),
        Patch: EventPatch.Parse(Str(r, 7, "{}")),
        Original: EventOriginal.Parse(Str(r, 8, "{}")),
        Reason: Str(r, 9),
        Detector: Str(r, 10),
        DetectorVersion: r.IsDBNull(11) ? null : r.GetInt32(11),
        AppVersion: Str(r, 12),
        SupersedesId: r.IsDBNull(13) ? null : r.GetInt64(13),
        RebasedFrom: Str(r, 14),
        DeltaS: r.IsDBNull(15) ? null : r.GetInt32(15),
        State: Str(r, 16, CorrectionStates.Active),
        AppliedEventId: r.IsDBNull(17) ? null : r.GetInt64(17),
        AppliedAt: r.IsDBNull(18) ? null : r.GetInt64(18),
        ApplyError: Str(r, 19),
        ShareState: Str(r, 20, CorrectionShareStates.Held),
        SharedAt: r.IsDBNull(21) ? null : r.GetInt64(21),
        CreatedAt: r.IsDBNull(22) ? 0 : r.GetInt64(22),
        UpdatedAt: r.IsDBNull(23) ? 0 : r.GetInt64(23));

    /// <summary>Applicable rows for a game: op &lt;&gt; 'revert' AND state IN ('active','absorbed','orphaned'), id ASC.</summary>
    public static Task<List<EventCorrection>> LoadApplicableAsync(SqliteConnection conn, SqliteTransaction? tx, long gameId)
    {
        using var cmd = Command(conn, tx,
            $"SELECT {SelectColumns} FROM event_corrections WHERE game_id = @g AND {ApplicablePredicate} ORDER BY id ASC");
        cmd.Parameters.AddWithValue("@g", gameId);
        return ReadAllAsync(cmd);
    }

    /// <summary>Applicable rows whose applied_event_id is <paramref name="eventId"/>, id ASC.</summary>
    public static Task<List<EventCorrection>> LoadByAppliedEventAsync(SqliteConnection conn, SqliteTransaction? tx, long eventId)
    {
        using var cmd = Command(conn, tx,
            $"SELECT {SelectColumns} FROM event_corrections WHERE applied_event_id = @e AND {ApplicablePredicate} ORDER BY id ASC");
        cmd.Parameters.AddWithValue("@e", eventId);
        return ReadAllAsync(cmd);
    }

    public static async Task<EventCorrection?> FindByCorrectionIdAsync(SqliteConnection conn, SqliteTransaction? tx, string correctionId)
    {
        using var cmd = Command(conn, tx,
            $"SELECT {SelectColumns} FROM event_corrections WHERE correction_id = @c ORDER BY id ASC LIMIT 1");
        cmd.Parameters.AddWithValue("@c", correctionId);
        return (await ReadAllAsync(cmd)).FirstOrDefault();
    }

    /// <summary>One ledger row by primary key (any state, any op), or null.</summary>
    public static async Task<EventCorrection?> FindByIdAsync(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = Command(conn, tx, $"SELECT {SelectColumns} FROM event_corrections WHERE id = @id LIMIT 1");
        cmd.Parameters.AddWithValue("@id", id);
        return (await ReadAllAsync(cmd)).FirstOrDefault();
    }

    /// <summary>The newest applicable row on a subject, or null.</summary>
    public static async Task<EventCorrection?> FindActiveBySubjectAsync(SqliteConnection conn, SqliteTransaction? tx, long gameId, string subjectKey)
    {
        using var cmd = Command(conn, tx,
            $"SELECT {SelectColumns} FROM event_corrections WHERE game_id = @g AND subject_key = @k AND {ApplicablePredicate} ORDER BY id DESC LIMIT 1");
        cmd.Parameters.AddWithValue("@g", gameId);
        cmd.Parameters.AddWithValue("@k", subjectKey);
        return (await ReadAllAsync(cmd)).FirstOrDefault();
    }

    /// <summary>INSERT ... RETURNING id. Id on the record is ignored.</summary>
    public static async Task<long> InsertAsync(SqliteConnection conn, SqliteTransaction? tx, EventCorrection row)
    {
        using var cmd = Command(conn, tx, """
            INSERT INTO event_corrections (
                correction_id, game_id, subject_key, subject_type, subject_time_s, op, patch, original, reason,
                detector, detector_v, app_version, supersedes_id, rebased_from, delta_s, state, applied_event_id,
                applied_at, apply_error, share_state, shared_at, created_at, updated_at)
            VALUES (
                @cid, @g, @sk, @st, @sts, @op, @patch, @orig, @reason,
                @det, @detv, @app, @sup, @rebased, @delta, @state, @applied,
                @appliedAt, @err, @share, @sharedAt, @created, @updated)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue("@cid", row.CorrectionId);
        cmd.Parameters.AddWithValue("@g", row.GameId);
        cmd.Parameters.AddWithValue("@sk", row.SubjectKey);
        cmd.Parameters.AddWithValue("@st", row.SubjectType);
        cmd.Parameters.AddWithValue("@sts", row.SubjectTimeS);
        cmd.Parameters.AddWithValue("@op", row.Op);
        cmd.Parameters.AddWithValue("@patch", row.Patch.ToJson());
        cmd.Parameters.AddWithValue("@orig", row.Original.ToJson());
        cmd.Parameters.AddWithValue("@reason", row.Reason ?? "");
        cmd.Parameters.AddWithValue("@det", row.Detector ?? "");
        cmd.Parameters.AddWithValue("@detv", (object?)row.DetectorVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@app", row.AppVersion ?? "");
        cmd.Parameters.AddWithValue("@sup", (object?)row.SupersedesId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rebased", row.RebasedFrom ?? "");
        cmd.Parameters.AddWithValue("@delta", (object?)row.DeltaS ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@state", row.State);
        cmd.Parameters.AddWithValue("@applied", (object?)row.AppliedEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@appliedAt", (object?)row.AppliedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@err", row.ApplyError ?? "");
        cmd.Parameters.AddWithValue("@share", string.IsNullOrEmpty(row.ShareState) ? CorrectionShareStates.Held : row.ShareState);
        cmd.Parameters.AddWithValue("@sharedAt", (object?)row.SharedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", row.CreatedAt);
        cmd.Parameters.AddWithValue("@updated", row.UpdatedAt);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>UPDATE state, applied_event_id, apply_error, updated_at; applied_at = now when appliedEventId is not null.</summary>
    public static async Task SetStateAsync(SqliteConnection conn, SqliteTransaction? tx, long id, string state, long? appliedEventId, string applyError = "")
    {
        using var cmd = Command(conn, tx, """
            UPDATE event_corrections
            SET state = @state,
                applied_event_id = @applied,
                applied_at = CASE WHEN @applied IS NULL THEN applied_at ELSE @now END,
                apply_error = @err,
                updated_at = @now
            WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@applied", (object?)appliedEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@err", applyError ?? "");
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A fuzzy match moved the subject: record the new key and where it came from.</summary>
    public static async Task RebaseSubjectAsync(SqliteConnection conn, SqliteTransaction? tx, long id, string newSubjectKey, string rebasedFrom)
    {
        using var cmd = Command(conn, tx,
            "UPDATE event_corrections SET subject_key = @k, rebased_from = @from, updated_at = @now WHERE id = @id");
        cmd.Parameters.AddWithValue("@k", newSubjectKey);
        cmd.Parameters.AddWithValue("@from", rebasedFrom ?? "");
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>game_events rows of a game that carry a correction marker: (Id, EventType, GameTimeS, Details, EventKey).</summary>
    public static Task<List<GameEvent>> LoadMarkedRowsAsync(SqliteConnection conn, SqliteTransaction? tx, long gameId)
    {
        using var cmd = Command(conn, tx, $"""
            SELECT id, game_id, event_type, game_time_s, details, event_key
            FROM game_events
            WHERE game_id = @g AND {IsCorrectedPredicate}
            ORDER BY game_time_s, id
            """);
        cmd.Parameters.AddWithValue("@g", gameId);
        return ReadRowsAsync(cmd);
    }

    /// <summary>All rows of a game (id, game_id, event_type, game_time_s, details, event_key) ORDER BY game_time_s, id.</summary>
    public static Task<List<GameEvent>> LoadRowsAsync(SqliteConnection conn, SqliteTransaction? tx, long gameId)
    {
        using var cmd = Command(conn, tx, """
            SELECT id, game_id, event_type, game_time_s, details, event_key
            FROM game_events
            WHERE game_id = @g
            ORDER BY game_time_s, id
            """);
        cmd.Parameters.AddWithValue("@g", gameId);
        return ReadRowsAsync(cmd);
    }

    public static async Task SetEventKeyAsync(SqliteConnection conn, SqliteTransaction? tx, long eventId, string eventKey)
    {
        using var cmd = Command(conn, tx, "UPDATE game_events SET event_key = @k WHERE id = @id");
        cmd.Parameters.AddWithValue("@k", eventKey);
        cmd.Parameters.AddWithValue("@id", eventId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Reads a game_events row projected as (id, game_id, event_type, game_time_s, details, event_key).</summary>
    public static GameEvent ReadRow(SqliteDataReader r) => new()
    {
        Id = r.IsDBNull(0) ? 0 : r.GetInt32(0),
        GameId = r.IsDBNull(1) ? 0 : r.GetInt64(1),
        EventType = r.IsDBNull(2) ? "" : r.GetString(2),
        GameTimeS = r.IsDBNull(3) ? 0 : r.GetInt32(3),
        Details = r.IsDBNull(4) ? "{}" : r.GetString(4),
        EventKey = r.IsDBNull(5) ? null : r.GetString(5),
    };

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static async Task<List<EventCorrection>> ReadAllAsync(SqliteCommand cmd)
    {
        var rows = new List<EventCorrection>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(Read(reader));
        return rows;
    }

    private static async Task<List<GameEvent>> ReadRowsAsync(SqliteCommand cmd)
    {
        var rows = new List<GameEvent>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(ReadRow(reader));
        return rows;
    }

    private static string Str(SqliteDataReader r, int i, string fallback = "") =>
        r.IsDBNull(i) ? fallback : r.GetString(i);
}

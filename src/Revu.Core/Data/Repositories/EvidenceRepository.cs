#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Constants;

namespace Revu.Core.Data.Repositories;

public sealed class EvidenceRepository : IEvidenceRepository
{
    private readonly IDbConnectionFactory _factory;

    private static long WindowStartUnixSeconds() =>
        DateTimeOffset.UtcNow.AddDays(-PatternConstants.WindowDays).ToUnixTimeSeconds();

    public EvidenceRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> UpsertAsync(EvidenceUpsert item)
    {
        var sourceKind = EvidenceKinds.Normalize(item.SourceKind);
        var polarity = EvidencePolarities.Normalize(item.Polarity);
        var status = EvidenceStatuses.Normalize(item.Status);
        var sourceKey = (item.SourceKey ?? "").Trim();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var conn = _factory.CreateConnection();
        long? existingId = null;
        long? existingObjectiveId = null;

        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            using var findCmd = conn.CreateCommand();
            findCmd.CommandText = """
                SELECT id, objective_id
                FROM evidence_items
                WHERE game_id = @gameId
                  AND source_kind = @sourceKind
                  AND source_key = @sourceKey
                LIMIT 1
                """;
            findCmd.Parameters.AddWithValue("@gameId", item.GameId);
            findCmd.Parameters.AddWithValue("@sourceKind", sourceKind);
            findCmd.Parameters.AddWithValue("@sourceKey", sourceKey);
            using var findReader = await findCmd.ExecuteReaderAsync();
            if (await findReader.ReadAsync())
            {
                existingId = findReader.GetInt64(0);
                existingObjectiveId = findReader.IsDBNull(1) ? null : findReader.GetInt64(1);
            }
        }

        if (existingId is long id)
        {
            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = """
                UPDATE evidence_items
                SET source_id = @sourceId,
                    start_time_s = @startTimeS,
                    end_time_s = @endTimeS,
                    title = @title,
                    note = CASE
                        WHEN TRIM(COALESCE(note, '')) = '' THEN @note
                        ELSE note
                    END,
                    objective_id = CASE
                        WHEN @objectiveId IS NULL THEN objective_id
                        ELSE @objectiveId
                    END,
                    prompt_id = CASE
                        WHEN @promptId IS NULL THEN prompt_id
                        ELSE @promptId
                    END,
                    concept_tag_id = CASE
                        WHEN @conceptTagId IS NULL THEN concept_tag_id
                        ELSE @conceptTagId
                    END,
                    matchup_note_id = CASE
                        WHEN @matchupNoteId IS NULL THEN matchup_note_id
                        ELSE @matchupNoteId
                    END,
                    polarity = CASE
                        WHEN polarity = 'neutral' AND @polarity != 'neutral' THEN @polarity
                        ELSE polarity
                    END,
                    status = CASE
                        WHEN status = 'needs_review' AND @status != 'needs_review' THEN @status
                        ELSE status
                    END,
                    updated_at = @updatedAt
                WHERE id = @id
                """;
            BindUpsert(updateCmd, item, sourceKind, sourceKey, polarity, status, now);
            updateCmd.Parameters.AddWithValue("@id", id);
            await updateCmd.ExecuteNonQueryAsync();

            // Award the clip bonus only when this upsert newly attaches the item to
            // an objective (the UPDATE only sets objective_id when @objectiveId is
            // non-null). Re-upserting an already-tagged item must not stack points.
            if (item.ObjectiveId.HasValue && item.ObjectiveId != existingObjectiveId)
            {
                await AwardClipScoreAsync(conn, item.ObjectiveId.Value);
            }
            return id;
        }

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO evidence_items
                (game_id, source_kind, source_id, source_key, start_time_s, end_time_s,
                 title, note, objective_id, prompt_id, concept_tag_id, matchup_note_id,
                 polarity, status, created_at, updated_at)
            VALUES
                (@gameId, @sourceKind, @sourceId, @sourceKey, @startTimeS, @endTimeS,
                 @title, @note, @objectiveId, @promptId, @conceptTagId, @matchupNoteId,
                 @polarity, @status, @createdAt, @updatedAt)
            """;
        BindUpsert(insertCmd, item, sourceKind, sourceKey, polarity, status, now);
        insertCmd.Parameters.AddWithValue("@createdAt", now);
        await insertCmd.ExecuteNonQueryAsync();

        // New evidence item created already attached to an objective → award the
        // clip bonus once.
        if (item.ObjectiveId.HasValue)
        {
            await AwardClipScoreAsync(conn, item.ObjectiveId.Value);
        }

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(await idCmd.ExecuteScalarAsync());
    }

    public async Task<IReadOnlyList<EvidenceItemRecord>> GetForGameAsync(long gameId, bool includeDismissed = false)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SelectEvidenceSql}
            WHERE e.game_id = @gameId
              {(includeDismissed ? "" : "AND e.status != 'dismissed'")}
            ORDER BY
                CASE e.status
                    WHEN 'needs_review' THEN 0
                    WHEN 'highlight' THEN 1
                    WHEN 'evidence' THEN 2
                    ELSE 3
                END,
                COALESCE(e.start_time_s, 2147483647),
                e.updated_at DESC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadEvidenceAsync(cmd);
    }

    public async Task<IReadOnlyList<EvidenceItemRecord>> GetForObjectiveAsync(long objectiveId, bool includeDismissed = false)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SelectEvidenceSql}
            WHERE e.objective_id = @objectiveId
              {(includeDismissed ? "" : "AND e.status != 'dismissed'")}
            ORDER BY e.updated_at DESC, e.created_at DESC
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        return await ReadEvidenceAsync(cmd);
    }

    public async Task<IReadOnlyList<EvidenceItemRecord>> GetRecentAsync(int limit = 20, bool includeDismissed = false)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SelectEvidenceSql}
            WHERE 1 = 1
              {(includeDismissed ? "" : "AND e.status != 'dismissed'")}
            ORDER BY e.updated_at DESC, e.created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));
        return await ReadEvidenceAsync(cmd);
    }

    public async Task<int> CountPendingAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM evidence_items WHERE status = 'needs_review'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public Task UpdateStatusAsync(long evidenceId, string status) =>
        UpdateScalarAsync(evidenceId, "status", EvidenceStatuses.Normalize(status));

    public Task UpdatePolarityAsync(long evidenceId, string polarity) =>
        UpdateScalarAsync(evidenceId, "polarity", EvidencePolarities.Normalize(polarity));

    public async Task DeleteAsync(long evidenceId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM evidence_items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", evidenceId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateObjectiveAsync(long evidenceId, long? objectiveId)
    {
        using var conn = _factory.CreateConnection();

        // v2.17.25: read the prior objective so we only award the clip bonus when
        // this item is newly tagged onto a (different) objective — re-saving the
        // same tag must not keep stacking points.
        long? priorObjectiveId = null;
        using (var readCmd = conn.CreateCommand())
        {
            readCmd.CommandText = "SELECT objective_id FROM evidence_items WHERE id = @id";
            readCmd.Parameters.AddWithValue("@id", evidenceId);
            var prior = await readCmd.ExecuteScalarAsync();
            if (prior is not null and not DBNull) priorObjectiveId = Convert.ToInt64(prior);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE evidence_items
                SET objective_id = @objectiveId,
                    updated_at = @updatedAt
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@objectiveId", objectiveId.HasValue ? objectiveId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@id", evidenceId);
            await cmd.ExecuteNonQueryAsync();
        }

        if (objectiveId.HasValue && objectiveId != priorObjectiveId)
        {
            await AwardClipScoreAsync(conn, objectiveId.Value);
        }
    }

    /// <summary>
    /// P-027: tag (or untag, when promptId is null) an evidence row to the custom
    /// prompt it answers. Mirrors UpdateObjectiveAsync's shape but carries NO score
    /// award — prompt grouping is purely organizational, the objective_id path
    /// remains the (separate) score-bearing one. objective_id is left untouched.
    /// </summary>
    public async Task UpdatePromptAsync(long evidenceId, long? promptId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE evidence_items
            SET prompt_id = @promptId,
                updated_at = @updatedAt
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@promptId", promptId.HasValue ? promptId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", evidenceId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// v2.17.25: attaching a clip / bookmark / moment to an objective adds points
    /// to that objective's score — reviewing footage on a focus counts toward
    /// learning it, not just playing games. Forward-only and additive: we never
    /// dock points when a clip is detached, and existing clips/games are left
    /// untouched (no retroactive backfill).
    /// </summary>
    private const int ClipScorePoints = 2;

    private static async Task AwardClipScoreAsync(SqliteConnection conn, long objectiveId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE objectives SET score = MAX(0, score + @pts) WHERE id = @objectiveId";
        cmd.Parameters.AddWithValue("@pts", ClipScorePoints);
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateNoteAsync(long evidenceId, string note)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE evidence_items
            SET note = @note,
                updated_at = @updatedAt
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@note", note ?? "");
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", evidenceId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AttachClipToEvidenceAsync(long evidenceId, long bookmarkId, int clipStartS, int clipEndS)
    {
        // Convert an existing evidence row (e.g. an auto-detected pattern moment)
        // into the saved-clip backed by the given bookmark — same shape the VOD
        // player produces — so the moment surfaces as a real clip in the review
        // instead of duplicating it with a second evidence row. Promotes a still-
        // pending row to 'evidence' so it leaves the needs-review queue.
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE evidence_items
            SET source_kind = '{EvidenceKinds.Clip}',
                source_id = @bookmarkId,
                source_key = @sourceKey,
                start_time_s = @startS,
                end_time_s = @endS,
                status = CASE WHEN status = '{EvidenceStatuses.NeedsReview}' THEN '{EvidenceStatuses.Evidence}' ELSE status END,
                updated_at = @updatedAt
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@bookmarkId", bookmarkId);
        cmd.Parameters.AddWithValue("@sourceKey", $"clip:{bookmarkId}");
        cmd.Parameters.AddWithValue("@startS", clipStartS);
        cmd.Parameters.AddWithValue("@endS", clipEndS);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", evidenceId);
        await cmd.ExecuteNonQueryAsync();
    }

    // A saved bookmark and its clip-evidence row describe ONE user-selected
    // moment. Prefer the latest linked evidence row, then include legacy clips
    // without bookmarks. Raw events and failed-criterion anchors never enter
    // this set, even if they happen to track an active objective's event type.
    // Evidence owns the live tag, note and rating when present, including null
    // or empty values from explicit clears; stale bookmark fields cannot revive them.
    private const string SavedPatternMomentsSql = """
        WITH saved_moments AS (
            SELECT COALESCE(e.id, 0) AS evidence_id,
                   b.id AS bookmark_id,
                   b.game_id,
                   CASE WHEN e.id IS NOT NULL THEN e.objective_id ELSE b.objective_id END AS objective_id,
                   CASE WHEN b.clip_start_s IS NOT NULL AND b.clip_end_s > b.clip_start_s
                        THEN b.clip_start_s ELSE b.game_time_s END AS start_time_s,
                   CASE WHEN b.clip_start_s IS NOT NULL AND b.clip_end_s > b.clip_start_s
                        THEN b.clip_end_s ELSE b.game_time_s END AS end_time_s,
                   COALESCE(NULLIF(TRIM(e.title), ''),
                       CASE WHEN b.clip_start_s IS NOT NULL AND b.clip_end_s > b.clip_start_s
                            THEN 'Saved clip' ELSE 'Bookmark' END) AS title,
                   CASE WHEN e.id IS NOT NULL THEN COALESCE(e.note, '') ELSE COALESCE(b.note, '') END AS note,
                   CASE WHEN e.id IS NOT NULL THEN COALESCE(e.polarity, 'neutral')
                        ELSE COALESCE(NULLIF(b.quality, ''), 'neutral') END AS polarity,
                   CASE WHEN b.clip_start_s IS NOT NULL AND b.clip_end_s > b.clip_start_s
                        THEN 'clip' ELSE 'bookmark' END AS source_kind,
                   COALESCE(b.created_at, e.created_at, 0) AS created_at,
                   COALESCE(b.clip_path, '') AS clip_path
            FROM vod_bookmarks b
            LEFT JOIN evidence_items e ON e.id = (
                SELECT linked.id FROM evidence_items linked
                WHERE linked.game_id = b.game_id
                  AND linked.source_kind = 'clip' AND linked.source_id = b.id
                ORDER BY linked.updated_at DESC, linked.id DESC
                LIMIT 1)
            WHERE COALESCE(e.status, '') != 'dismissed'

            UNION ALL

            SELECT e.id, NULL, e.game_id, e.objective_id,
                   e.start_time_s, e.end_time_s,
                   COALESCE(e.title, ''), COALESCE(e.note, ''),
                   COALESCE(e.polarity, 'neutral'), 'clip',
                   COALESCE(e.created_at, 0), ''
            FROM evidence_items e
            WHERE e.source_kind = 'clip' AND e.status != 'dismissed'
              AND NOT EXISTS (
                  SELECT 1 FROM vod_bookmarks b
                  WHERE b.id = e.source_id AND b.game_id = e.game_id)
              AND (e.source_id IS NULL OR e.id = (
                  SELECT duplicate.id FROM evidence_items duplicate
                  WHERE duplicate.source_kind = 'clip'
                    AND duplicate.game_id = e.game_id AND duplicate.source_id = e.source_id
                  ORDER BY duplicate.updated_at DESC, duplicate.id DESC
                  LIMIT 1))
        ), eligible_moments AS (
            SELECT s.*, COALESCE(o.title, '') AS objective_title,
                   COALESCE(g.champion_name, '') AS champion_name,
                   COALESCE(g.win, 0) AS win,
                   COALESCE(g.timestamp, 0) AS game_timestamp,
                   COALESCE(g.game_duration, 0) AS game_duration,
                   COALESCE(v.file_path, '') AS vod_path
            FROM saved_moments s
            JOIN objectives o ON o.id = s.objective_id AND o.status = 'active'
            JOIN games g ON g.game_id = s.game_id
            LEFT JOIN vod_files v ON v.game_id = s.game_id
            WHERE COALESCE(g.is_hidden, 0) = 0
        )
        """;

    /// <summary>
    /// Revisit the clips/bookmarks explicitly saved for current objectives, and
    /// surface repeated bad examples only when they span distinct recent games.
    /// Saving a moment is the selection signal; raw event frequency is not.
    /// </summary>
    public async Task<IReadOnlyList<ObjectivePatternCard>> GetPatternCardsAsync(int limit = 6)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SavedPatternMomentsSql}
            SELECT objective_id, objective_title,
                   COUNT(*) AS saved_count, COUNT(DISTINCT game_id) AS saved_games,
                   SUM(CASE WHEN polarity = 'bad' AND game_timestamp >= @windowStart THEN 1 ELSE 0 END) AS bad_count,
                   COUNT(DISTINCT CASE WHEN polarity = 'bad' AND game_timestamp >= @windowStart THEN game_id END) AS bad_games
            FROM eligible_moments
            GROUP BY objective_id
            """;
        cmd.Parameters.AddWithValue("@windowStart", WindowStartUnixSeconds());

        var cards = new List<ObjectivePatternCard>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var objectiveId = reader.GetInt64(0);
            var title = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(title)) title = "Objective";
            var savedCount = reader.GetInt32(2);
            var savedGames = reader.GetInt32(3);
            var badCount = reader.GetInt32(4);
            var badGames = reader.GetInt32(5);
            cards.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindSavedObjectiveEvidence,
                Title: title,
                Detail: $"Revisit {savedCount} saved clips and bookmarks across {savedGames} games.",
                ObjectiveId: objectiveId,
                Severity: "low",
                MomentCount: savedCount,
                GameCount: savedGames));

            if (badCount >= PatternConstants.BadObjectiveMinBad && badGames >= PatternConstants.BadObjectiveMinGames)
            {
                cards.Add(new ObjectivePatternCard(
                    Kind: PatternConstants.KindBadObjectiveEvidence,
                    Title: $"{title}: recurring mistakes",
                    Detail: $"{badCount} saved moments marked bad across {badGames} games in the last {PatternConstants.WindowDays} days.",
                    ObjectiveId: objectiveId,
                    Severity: badCount >= PatternConstants.BadObjectiveHighBad ? "high" : "medium",
                    MomentCount: badCount,
                    GameCount: badGames));
            }
        }

        return cards
            .OrderBy(static c => c.Severity == "high" ? 0 : c.Severity == "medium" ? 1 : 2)
            .ThenByDescending(static c => c.GameCount)
            .ThenByDescending(static c => c.MomentCount)
            .ThenBy(static c => c.ObjectiveId)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    public async Task<IReadOnlyList<PatternMoment>> GetPatternMomentsAsync(ObjectivePatternCard pattern)
    {
        if (pattern.ObjectiveId is not long objectiveId
            || pattern.Kind is not (PatternConstants.KindSavedObjectiveEvidence or PatternConstants.KindBadObjectiveEvidence))
        {
            // Old event/criterion keys can still exist in review history; they
            // must not reopen a raw-event playlist after the selection redesign.
            return Array.Empty<PatternMoment>();
        }

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var trend = pattern.Kind == PatternConstants.KindBadObjectiveEvidence;
        cmd.CommandText = $"""
            {SavedPatternMomentsSql}
            SELECT evidence_id, game_id, champion_name, win, game_timestamp,
                   start_time_s, end_time_s, title, note, polarity, source_kind,
                   vod_path, created_at, clip_path, bookmark_id, game_duration
            FROM eligible_moments
            WHERE objective_id = @objectiveId
              {(trend ? "AND polarity = 'bad' AND game_timestamp >= @windowStart" : "")}
            ORDER BY game_timestamp ASC, COALESCE(start_time_s, 2147483647) ASC,
                     COALESCE(bookmark_id, evidence_id) ASC
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        if (trend) cmd.Parameters.AddWithValue("@windowStart", WindowStartUnixSeconds());

        var moments = new List<PatternMoment>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            moments.Add(new PatternMoment(
                EvidenceId: reader.GetInt64(0),
                GameId: reader.GetInt64(1),
                ChampionName: reader.GetString(2),
                Win: reader.GetInt64(3) != 0,
                GameTimestamp: reader.GetInt64(4),
                StartTimeSeconds: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                EndTimeSeconds: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Title: reader.GetString(7),
                Note: reader.GetString(8),
                Polarity: EvidencePolarities.Normalize(reader.GetString(9)),
                SourceKind: reader.GetString(10),
                VodPath: reader.GetString(11),
                CreatedAt: reader.GetInt64(12),
                ClipPath: reader.GetString(13),
                BookmarkId: reader.IsDBNull(14) ? null : reader.GetInt64(14),
                GameDurationSeconds: reader.GetInt32(15)));
        }
        return moments;
    }

    public async Task MarkPatternReviewedAsync(string patternKey, string kind, int momentCount)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pattern_reviews (pattern_key, kind, moment_count, reviewed_at)
            VALUES (@key, @kind, @count, @now)
            ON CONFLICT(pattern_key) DO UPDATE SET
                kind = excluded.kind,
                moment_count = excluded.moment_count,
                reviewed_at = excluded.reviewed_at
            """;
        cmd.Parameters.AddWithValue("@key", patternKey);
        cmd.Parameters.AddWithValue("@kind", kind ?? "");
        cmd.Parameters.AddWithValue("@count", momentCount);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountReviewedPatternsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pattern_reviews";
        var result = await cmd.ExecuteScalarAsync();
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetReviewedPatternsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pattern_key, COALESCE(reviewed_at, 0) FROM pattern_reviews";
        var stamps = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0)) stamps[reader.GetString(0)] = reader.GetInt64(1);
        }
        return stamps;
    }

    // ── Pattern-evidence materializer support ───────────────────────────────

    /// <summary>
    /// Find a promoted twin of a materialized moment: a clip-promoted row with
    /// the same title and the EXACT original window. Guards the region/gank
    /// upserts against re-materialization duplicating a moment the user promoted
    /// (the rekey moves it out from under its source key, but the note endpoint
    /// clips the moment's own start/end verbatim, so title + exact window is a
    /// stable identity).
    /// </summary>
    public async Task<long?> FindPromotedTwinAsync(long gameId, string title, int startTimeSeconds, int endTimeSeconds)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.id
            FROM evidence_items e
            WHERE e.game_id = @gameId
              AND e.source_kind = '{EvidenceKinds.Clip}'
              AND e.title = @title
              AND e.start_time_s = @startS
              AND e.end_time_s = @endS
            ORDER BY e.id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@startS", startTimeSeconds);
        cmd.Parameters.AddWithValue("@endS", endTimeSeconds);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    /// <summary>
    /// Delete the evidence row addressed by its dedupe identity. Returns rows
    /// affected — 0 when no such row exists (e.g. it was promoted to a clip and
    /// rekeyed, which deliberately protects the user's clip from deletion).
    /// </summary>
    public async Task<int> DeleteBySourceKeyAsync(long gameId, string sourceKind, string sourceKey)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM evidence_items
            WHERE game_id = @gameId AND source_kind = @sourceKind AND source_key = @sourceKey
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@sourceKind", sourceKind);
        cmd.Parameters.AddWithValue("@sourceKey", sourceKey);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateScalarAsync(long evidenceId, string columnName, string value)
    {
        if (columnName is not ("status" or "polarity"))
        {
            throw new ArgumentOutOfRangeException(nameof(columnName));
        }

        using var conn = _factory.CreateConnection();
        using var tx = columnName == "polarity" ? conn.BeginTransaction() : null;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE evidence_items
            SET {columnName} = @value,
                updated_at = @updatedAt
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", evidenceId);
        await cmd.ExecuteNonQueryAsync();

        if (columnName == "polarity")
        {
            // Rating an evidence card must also update the clip's stored quality;
            // otherwise the VOD and objective views disagree about the same clip.
            using var bookmark = conn.CreateCommand();
            bookmark.Transaction = tx;
            bookmark.CommandText = """
                UPDATE vod_bookmarks
                SET quality = @polarity
                WHERE EXISTS (
                    SELECT 1 FROM evidence_items e
                    WHERE e.id = @id AND e.source_kind = @kind
                      AND e.source_id = vod_bookmarks.id
                      AND e.game_id = vod_bookmarks.game_id
                )
                """;
            bookmark.Parameters.AddWithValue("@polarity", value);
            bookmark.Parameters.AddWithValue("@kind", EvidenceKinds.Clip);
            bookmark.Parameters.AddWithValue("@id", evidenceId);
            await bookmark.ExecuteNonQueryAsync();
            tx!.Commit();
        }
    }

    private static void BindUpsert(
        SqliteCommand cmd,
        EvidenceUpsert item,
        string sourceKind,
        string sourceKey,
        string polarity,
        string status,
        long now)
    {
        cmd.Parameters.AddWithValue("@gameId", item.GameId);
        cmd.Parameters.AddWithValue("@sourceKind", sourceKind);
        cmd.Parameters.AddWithValue("@sourceId", item.SourceId.HasValue ? item.SourceId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceKey", sourceKey);
        cmd.Parameters.AddWithValue("@startTimeS", item.StartTimeSeconds.HasValue ? item.StartTimeSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@endTimeS", item.EndTimeSeconds.HasValue ? item.EndTimeSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@title", item.Title ?? "");
        cmd.Parameters.AddWithValue("@note", item.Note ?? "");
        cmd.Parameters.AddWithValue("@objectiveId", item.ObjectiveId.HasValue ? item.ObjectiveId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@promptId", item.PromptId.HasValue ? item.PromptId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@conceptTagId", item.ConceptTagId.HasValue ? item.ConceptTagId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@matchupNoteId", item.MatchupNoteId.HasValue ? item.MatchupNoteId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@polarity", polarity);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@updatedAt", now);
    }

    private const string SelectEvidenceSql = """
        SELECT e.id,
               e.game_id,
               e.source_kind,
               e.source_id,
               COALESCE(e.source_key, ''),
               e.start_time_s,
               e.end_time_s,
               COALESCE(e.title, ''),
               COALESCE(e.note, ''),
               e.objective_id,
               COALESCE(o.title, ''),
               e.concept_tag_id,
               COALESCE(c.name, ''),
               e.matchup_note_id,
               COALESCE(e.polarity, 'neutral'),
               COALESCE(e.status, 'needs_review'),
               e.created_at,
               e.updated_at,
               COALESCE(g.champion_name, ''),
               g.win,
               g.timestamp,
               e.prompt_id
        FROM evidence_items e
        LEFT JOIN objectives o ON o.id = e.objective_id
        LEFT JOIN concept_tags c ON c.id = e.concept_tag_id
        LEFT JOIN games g ON g.game_id = e.game_id
        """;

    private static async Task<IReadOnlyList<EvidenceItemRecord>> ReadEvidenceAsync(SqliteCommand cmd)
    {
        var rows = new List<EvidenceItemRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new EvidenceItemRecord(
                Id: reader.GetInt64(0),
                GameId: reader.GetInt64(1),
                SourceKind: reader.GetString(2),
                SourceId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                SourceKey: reader.GetString(4),
                StartTimeSeconds: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                EndTimeSeconds: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Title: reader.GetString(7),
                Note: reader.GetString(8),
                ObjectiveId: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                ObjectiveTitle: reader.GetString(10),
                ConceptTagId: reader.IsDBNull(11) ? null : reader.GetInt64(11),
                ConceptTagName: reader.GetString(12),
                MatchupNoteId: reader.IsDBNull(13) ? null : reader.GetInt64(13),
                Polarity: reader.GetString(14),
                Status: reader.GetString(15),
                CreatedAt: reader.IsDBNull(16) ? null : reader.GetInt64(16),
                UpdatedAt: reader.IsDBNull(17) ? null : reader.GetInt64(17),
                ChampionName: reader.GetString(18),
                Win: reader.IsDBNull(19) ? null : reader.GetInt64(19) != 0,
                GameTimestamp: reader.IsDBNull(20) ? null : reader.GetInt64(20),
                PromptId: reader.IsDBNull(21) ? null : reader.GetInt64(21)));
        }

        return rows;
    }
}

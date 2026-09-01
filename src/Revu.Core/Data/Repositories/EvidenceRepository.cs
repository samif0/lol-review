#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Constants;

namespace Revu.Core.Data.Repositories;

public sealed class EvidenceRepository : IEvidenceRepository
{
    private readonly IDbConnectionFactory _factory;

    // The ONE game gate every pattern count, moment playlist, and backfill query
    // shares: visible ranked/manual games inside the recency window. Reviewing,
    // rating, or skipping a game has NO hiding effect — the predecessor predicate
    // (13 ANDed "game is completely unreviewed" conditions) meant the app's own
    // review flow permanently ejected evidence from every pattern count, which is
    // why the Patterns page sat empty for anyone who actually used the app.
    // @windowStart = now - PatternConstants.WindowDays, unix seconds.
    private const string PatternGamePredicate = """
        COALESCE(g.is_hidden, 0) = 0
        AND COALESCE(g.queue_type, '') IN ('Ranked Solo/Duo', 'Manual')
        AND COALESCE(g.timestamp, 0) >= @windowStart
        """;

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

    /// <summary>
    /// The objective-driven pattern detectors (v3.6: patterns exist ONLY where
    /// they concern a learning objective the player set — the v3.5 predefined
    /// heuristics were retired by product decision; see PatternConstants).
    /// Three kinds: bad-tagged clips per objective, an active objective's
    /// structured criterion failing across recent games, and recurrences of the
    /// event tokens an objective tracks.
    /// </summary>
    public async Task<IReadOnlyList<ObjectivePatternCard>> GetPatternCardsAsync(int limit = 6)
    {
        var cards = new List<ObjectivePatternCard>();
        var windowStart = WindowStartUnixSeconds();
        using var conn = _factory.CreateConnection();

        await AddBadObjectiveEvidenceCardsAsync(conn, windowStart, cards);
        await AddObjectiveCriteriaCardsAsync(conn, windowStart, cards);
        await AddObjectiveEventCardsAsync(conn, windowStart, cards);

        // Worst first (severity, then volume) under the card cap.
        return cards
            .OrderBy(static c => c.Severity == "high" ? 0 : c.Severity == "medium" ? 1 : 2)
            .ThenByDescending(static c => c.MomentCount)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    private static async Task AddBadObjectiveEvidenceCardsAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.objective_id,
                   COALESCE(o.title, '') AS objective_title,
                   SUM(CASE WHEN e.polarity = 'bad' THEN 1 ELSE 0 END) AS bad_count,
                   SUM(CASE WHEN e.polarity = 'good' THEN 1 ELSE 0 END) AS good_count,
                   COUNT(DISTINCT CASE WHEN e.polarity = 'bad' THEN e.game_id END) AS bad_games
            FROM evidence_items e
            JOIN games g ON g.game_id = e.game_id
            LEFT JOIN objectives o ON o.id = e.objective_id
            WHERE e.objective_id IS NOT NULL
              AND e.status != 'dismissed'
              AND {PatternGamePredicate}
            GROUP BY e.objective_id
            HAVING bad_count >= @minBad AND bad_count > good_count
            ORDER BY bad_count DESC
            LIMIT @cardLimit
            """;
        cmd.Parameters.AddWithValue("@minBad", PatternConstants.BadObjectiveMinBad);
        cmd.Parameters.AddWithValue("@cardLimit", PatternConstants.BadObjectiveCardLimit);
        cmd.Parameters.AddWithValue("@windowStart", windowStart);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var objectiveId = reader.GetInt64(0);
            var title = reader.IsDBNull(1) || reader.GetString(1).Length == 0 ? "Objective" : reader.GetString(1);
            var badCount = Convert.ToInt32(reader.GetInt64(2));
            var goodCount = Convert.ToInt32(reader.GetInt64(3));
            var badGames = Convert.ToInt32(reader.GetInt64(4));
            cards.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindBadObjectiveEvidence,
                Title: $"{title}: mostly bad examples",
                Detail: $"{badCount} bad vs {goodCount} good clips on {title} "
                    + $"in the last {PatternConstants.WindowDays} days.",
                ObjectiveId: objectiveId,
                Severity: badCount >= PatternConstants.BadObjectiveHighBad ? "high" : "medium",
                // MomentCount = the bad rows only, matching the playlist filter.
                MomentCount: badCount,
                GameCount: badGames));
        }
    }

    /// <summary>
    /// An ACTIVE objective whose structured criterion keeps failing across
    /// recent games. Counts the materialized per-game anchor rows (so the card
    /// count always equals its playlist), each gated on the LIVE
    /// game_objectives row still saying criteria_met = 0 — a re-evaluation that
    /// passes, or archiving the objective, drops the game from count and
    /// playlist symmetrically.
    /// </summary>
    private static async Task AddObjectiveCriteriaCardsAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        var candidates = new List<(long ObjectiveId, int Fails, long? LatestGameId)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT e.source_key, COUNT(*) AS fails, MAX(e.game_id)
                FROM evidence_items e
                JOIN games g ON g.game_id = e.game_id
                WHERE e.source_kind = '{EvidenceKinds.TimelineRegion}'
                  AND e.source_key LIKE '{PatternConstants.ObjCritSourceKeyPrefix}%'
                  AND e.status != 'dismissed'
                  AND EXISTS (
                        SELECT 1 FROM game_objectives go
                        JOIN objectives o ON o.id = go.objective_id
                        WHERE go.game_id = e.game_id
                          AND '{PatternConstants.ObjCritSourceKeyPrefix}' || go.objective_id = e.source_key
                          AND go.criteria_met = 0
                          AND o.status = 'active')
                  AND {PatternGamePredicate}
                GROUP BY e.source_key
                HAVING fails >= @minFails
                ORDER BY fails DESC
                """;
            cmd.Parameters.AddWithValue("@minFails", PatternConstants.ObjCritMinFails);
            cmd.Parameters.AddWithValue("@windowStart", windowStart);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sourceKey = reader.GetString(0);
                if (!long.TryParse(sourceKey[PatternConstants.ObjCritSourceKeyPrefix.Length..], out var oid))
                {
                    continue;
                }
                candidates.Add((
                    oid,
                    Convert.ToInt32(reader.GetInt64(1)),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }

        var added = 0;
        foreach (var (objectiveId, fails, latestGameId) in candidates)
        {
            if (added >= PatternConstants.ObjCritCardLimit) break;

            // Share calibration: the criterion must be failing in a meaningful
            // share of the window games it was actually evaluated on.
            string title;
            int evaluated;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT COALESCE(o.title, ''),
                           (SELECT COUNT(go.criteria_met)
                            FROM game_objectives go
                            JOIN games g ON g.game_id = go.game_id
                            WHERE go.objective_id = o.id
                              AND go.criteria_met IS NOT NULL
                              AND {PatternGamePredicate})
                    FROM objectives o
                    WHERE o.id = @objectiveId
                    """;
                cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
                cmd.Parameters.AddWithValue("@windowStart", windowStart);
                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    continue;
                }
                title = reader.IsDBNull(0) || reader.GetString(0).Length == 0 ? "Objective" : reader.GetString(0);
                evaluated = Convert.ToInt32(reader.GetInt64(1));
            }

            var failShare = fails / (double)Math.Max(1, evaluated);
            if (failShare < PatternConstants.ObjCritMinFailShare)
            {
                continue;
            }

            cards.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindObjectiveCriteria,
                Title: $"{title}: criterion keeps failing",
                Detail: $"Missed the '{title}' criterion in {fails} of {evaluated} evaluated games "
                    + $"in the last {PatternConstants.WindowDays} days.",
                GameId: latestGameId,
                ObjectiveId: objectiveId,
                Severity: failShare >= PatternConstants.ObjCritHighFailShare ? "high" : "medium",
                MomentCount: fails,
                GameCount: fails));
            added++;
        }
    }

    /// <summary>
    /// Recurrence of an event token an ACTIVE objective tracks — the automatic
    /// heuristics return only where the player CHOSE to work on them (an
    /// objective tracking JUNGLE_GANK surfaces gank deaths; one tracking
    /// TEAMFIGHT surfaces fight clusters). Counts the materialized objev
    /// anchors plus clip-promoted moments (the note flow rekeys a promoted row
    /// but preserves its token-label title), both gated on the LIVE
    /// objective_event_types tie so untracking a token drops everything.
    /// </summary>
    private static async Task AddObjectiveEventCardsAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        var ties = new List<(long ObjectiveId, string ObjectiveTitle, string Token)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT et.objective_id, COALESCE(o.title, ''), et.event_token
                FROM objective_event_types et
                JOIN objectives o ON o.id = et.objective_id
                WHERE o.status = 'active'
                """;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ties.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var candidates = new List<ObjectivePatternCard>();
        foreach (var (objectiveId, objectiveTitle, rawToken) in ties)
        {
            var token = PatternConstants.Canonical(rawToken);
            if (token.Length == 0) continue;
            var label = PatternConstants.TokenLabel(token);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT COUNT(*), COUNT(DISTINCT e.game_id), MAX(e.game_id)
                FROM evidence_items e
                JOIN games g ON g.game_id = e.game_id
                WHERE e.status != 'dismissed'
                  AND (e.source_key LIKE @keyPrefix
                       OR (e.source_kind = '{EvidenceKinds.Clip}' AND e.title = @label))
                  AND {PatternGamePredicate}
                """;
            cmd.Parameters.AddWithValue("@keyPrefix", PatternConstants.ObjEventSourceKeyForToken(token) + "%");
            cmd.Parameters.AddWithValue("@label", label);
            cmd.Parameters.AddWithValue("@windowStart", windowStart);

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) continue;
            var count = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0));
            var games = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
            var latestGameId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            if (count < PatternConstants.ObjEventMinCount || games < PatternConstants.ObjEventMinGames)
            {
                continue;
            }

            var objTitle = objectiveTitle.Length == 0 ? "Objective" : objectiveTitle;
            candidates.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindObjectiveEvents,
                Title: $"{objTitle}: recurring {label}",
                Detail: $"{count} {label} moments across {games} games in the last "
                    + $"{PatternConstants.WindowDays} days — tracked by '{objTitle}'.",
                GameId: latestGameId,
                ObjectiveId: objectiveId,
                Severity: count >= PatternConstants.ObjEventHighCount ? "high" : "medium",
                Discriminator: token,
                MomentCount: count,
                GameCount: games));
        }

        cards.AddRange(candidates
            .OrderByDescending(static c => c.MomentCount)
            .Take(PatternConstants.ObjEventCardLimit));
    }

    // ── Pattern Review viewer ───────────────────────────────────────────────

    // Column projection shared by every pattern-moment query, joined to the
    // games row (INNER — the window predicate needs it; session_log is NOT
    // joined here: game_id has no unique constraint there, so joining it was a
    // latent row duplicator) plus vod_files (UNIQUE game_id) for the recording
    // path. Ordered oldest-first (game time, then in-game time with start-less
    // anchors last) so the viewer walks the pattern chronologically.
    private const string SelectPatternMomentSql = """
        SELECT e.id,
               e.game_id,
               COALESCE(g.champion_name, ''),
               COALESCE(g.win, 0),
               COALESCE(g.timestamp, 0),
               e.start_time_s,
               e.end_time_s,
               COALESCE(e.title, ''),
               COALESCE(e.note, ''),
               COALESCE(e.polarity, 'neutral'),
               COALESCE(e.source_kind, ''),
               COALESCE(v.file_path, ''),
               COALESCE(e.created_at, 0)
        FROM evidence_items e
        JOIN games g ON g.game_id = e.game_id
        LEFT JOIN vod_files v ON v.game_id = e.game_id
        """;

    private const string PatternMomentOrderBy =
        "ORDER BY g.timestamp ASC, COALESCE(e.start_time_s, 2147483647) ASC, e.id ASC";

    public async Task<IReadOnlyList<PatternMoment>> GetPatternMomentsAsync(ObjectivePatternCard pattern)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@windowStart", WindowStartUnixSeconds());

        // Each kind reuses the exact predicate from GetPatternCardsAsync so the
        // moment list is precisely the rows the card counted (pinned by tests:
        // card.MomentCount == playlist length).
        switch (pattern.Kind)
        {
            case PatternConstants.KindBadObjectiveEvidence:
                if (pattern.ObjectiveId is not long objId) return Array.Empty<PatternMoment>();
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.objective_id = @objectiveId
                      AND e.polarity = 'bad'
                      AND e.status != 'dismissed'
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                cmd.Parameters.AddWithValue("@objectiveId", objId);
                break;

            case PatternConstants.KindObjectiveCriteria:
            {
                // The per-game failed-criterion anchors, gated on the LIVE
                // game_objectives row (a re-evaluation that passes, or archiving
                // the objective, drops the game from count and playlist).
                if (pattern.ObjectiveId is not long critObjId) return Array.Empty<PatternMoment>();
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.source_kind = '{EvidenceKinds.TimelineRegion}'
                      AND e.source_key = @sourceKey
                      AND e.status != 'dismissed'
                      AND EXISTS (
                            SELECT 1 FROM game_objectives go
                            JOIN objectives o ON o.id = go.objective_id
                            WHERE go.game_id = e.game_id
                              AND go.objective_id = @critObjectiveId
                              AND go.criteria_met = 0
                              AND o.status = 'active')
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                cmd.Parameters.AddWithValue("@sourceKey", PatternConstants.ObjCritSourceKey(critObjId));
                cmd.Parameters.AddWithValue("@critObjectiveId", critObjId);
                break;
            }

            case PatternConstants.KindObjectiveEvents:
            {
                // Discriminator carries the tracked token. The objev anchors are
                // keyed by token+second; a moment the note flow promoted to a
                // clip lost that key but kept its token-label title, so the OR
                // branch counts it back in — the same predicate the card used,
                // keeping count == playlist. The tie EXISTS makes untracking the
                // token empty the playlist along with the card.
                var token = PatternConstants.Canonical(pattern.Discriminator);
                if (pattern.ObjectiveId is not long evObjId || token.Length == 0)
                {
                    return Array.Empty<PatternMoment>();
                }
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.status != 'dismissed'
                      AND (e.source_key LIKE @keyPrefix
                           OR (e.source_kind = '{EvidenceKinds.Clip}' AND e.title = @label))
                      AND EXISTS (
                            SELECT 1 FROM objective_event_types oet
                            JOIN objectives o ON o.id = oet.objective_id
                            WHERE oet.objective_id = @evObjectiveId
                              AND UPPER(oet.event_token) = @token
                              AND o.status = 'active')
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                cmd.Parameters.AddWithValue("@keyPrefix", PatternConstants.ObjEventSourceKeyForToken(token) + "%");
                cmd.Parameters.AddWithValue("@label", PatternConstants.TokenLabel(token));
                cmd.Parameters.AddWithValue("@evObjectiveId", evObjId);
                cmd.Parameters.AddWithValue("@token", token);
                break;
            }

            default:
                return Array.Empty<PatternMoment>();
        }

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
                StartTimeSeconds: reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetInt64(5)),
                EndTimeSeconds: reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetInt64(6)),
                Title: reader.GetString(7),
                Note: reader.GetString(8),
                Polarity: reader.GetString(9),
                SourceKind: reader.GetString(10),
                VodPath: reader.GetString(11),
                CreatedAt: reader.GetInt64(12)));
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
        using var cmd = conn.CreateCommand();
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

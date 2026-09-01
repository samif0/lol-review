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
    /// The seven windowed pattern detectors. Retired kinds: isolated_deaths
    /// (isolation is unprovable from the kill feed — the inference service's own
    /// comment disavows the label) and negative_matchup_clips (nothing ever
    /// wrote matchup_note_id, so it never fired once). Every kind here counts
    /// rows the CURRENT app actually produces — see PatternConstants for the
    /// shared writer/reader vocabulary and thresholds.
    /// </summary>
    public async Task<IReadOnlyList<ObjectivePatternCard>> GetPatternCardsAsync(int limit = 6)
    {
        var cards = new List<ObjectivePatternCard>();
        var windowStart = WindowStartUnixSeconds();
        using var conn = _factory.CreateConnection();

        await AddDeathClassMixCardsAsync(conn, windowStart, cards);
        await AddGankDeathsCardAsync(conn, windowStart, cards);
        await AddLostObjectiveFightsCardAsync(conn, windowStart, cards);
        await AddDeathsBeforeObjectivesCardAsync(conn, windowStart, cards);
        await AddBadObjectiveEvidenceCardsAsync(conn, windowStart, cards);
        await AddRecurringConceptTagCardsAsync(conn, windowStart, cards);
        await AddRuleBreaksCardAsync(conn, windowStart, cards);

        // Seven detectors under the card cap: worst first (severity, then volume).
        return cards
            .OrderBy(static c => c.Severity == "high" ? 0 : c.Severity == "medium" ? 1 : 2)
            .ThenByDescending(static c => c.MomentCount)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    /// <summary>
    /// One card per death-audit class ("Deaths to GREED") when a class both
    /// repeats in absolute terms and dominates the classified-death mix — the
    /// death-audit taxonomy's documented purpose ("44% of your deaths are
    /// vision-class") finally wired to the Patterns page.
    /// </summary>
    private static async Task AddDeathClassMixCardsAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        int classifiedTotal;
        using (var denomCmd = conn.CreateCommand())
        {
            denomCmd.CommandText = $"""
                SELECT COUNT(*)
                FROM death_classifications dc
                JOIN games g ON g.game_id = dc.game_id
                WHERE dc.death_class != ''
                  AND {PatternGamePredicate}
                """;
            denomCmd.Parameters.AddWithValue("@windowStart", windowStart);
            classifiedTotal = Convert.ToInt32(await denomCmd.ExecuteScalarAsync() ?? 0);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(e.title, ''),
                   COUNT(*) AS cnt,
                   COUNT(DISTINCT e.game_id) AS games,
                   MAX(e.game_id)
            FROM evidence_items e
            JOIN games g ON g.game_id = e.game_id
            WHERE e.status != 'dismissed'
              AND e.title LIKE @titlePrefix
              AND {PatternGamePredicate}
            GROUP BY e.title
            HAVING cnt >= @minCount AND games >= @minGames
            ORDER BY cnt DESC
            """;
        cmd.Parameters.AddWithValue("@titlePrefix", PatternConstants.DeathAuditTitlePrefix + "%");
        cmd.Parameters.AddWithValue("@minCount", PatternConstants.DeathClassMinCount);
        cmd.Parameters.AddWithValue("@minGames", PatternConstants.DeathClassMinGames);
        cmd.Parameters.AddWithValue("@windowStart", windowStart);

        var added = 0;
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync() && added < PatternConstants.DeathClassCardLimit)
        {
            var title = reader.GetString(0);
            var count = Convert.ToInt32(reader.GetInt64(1));
            var games = Convert.ToInt32(reader.GetInt64(2));
            var latestGameId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);

            // Reverse-map the title's chip label to its class key; skip titles
            // that don't resolve (e.g. a hand-typed clip note that happens to
            // start with "Death: "). The exact-title recheck matters: SQLite's
            // LIKE is case-insensitive, but the moment playlist matches the
            // canonical title exactly — a case-variant group would produce a
            // card whose count and playlist disagree.
            if (title.Length <= PatternConstants.DeathAuditTitlePrefix.Length)
            {
                continue;
            }
            var label = title[PatternConstants.DeathAuditTitlePrefix.Length..];
            var entry = DeathClasses.All.FirstOrDefault(c =>
                string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(entry.Key)
                || !string.Equals(title, PatternConstants.DeathAuditTitle(entry.Label), StringComparison.Ordinal))
            {
                continue;
            }

            var share = Math.Min(1.0, count / (double)Math.Max(1, classifiedTotal));
            if (share < PatternConstants.DeathClassMinShare)
            {
                continue;
            }

            var severity = count >= PatternConstants.DeathClassHighCount
                || share >= PatternConstants.DeathClassHighShare ? "high" : "medium";
            cards.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindDeathClassMix,
                Title: $"Deaths to {entry.Label}",
                Detail: $"{count} {entry.Label} deaths across {games} games — "
                    + $"{(int)Math.Round(share * 100)}% of your classified deaths in the last "
                    + $"{PatternConstants.WindowDays} days. {entry.Hint}.",
                GameId: latestGameId,
                Severity: severity,
                Discriminator: entry.Key,
                MomentCount: count,
                GameCount: games));
            added++;
        }
    }

    /// <summary>Laning-phase deaths to the enemy jungler — fully automatic
    /// (Details.jungle_gank is stamped at capture), so this kind fires with
    /// zero user input.</summary>
    private static async Task AddGankDeathsCardAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        var (count, games, latestGameId) = await CountWindowedTitleAsync(
            conn, windowStart, "=", PatternConstants.GankDeathTitle);
        if (count >= PatternConstants.GankMinCount && games >= PatternConstants.GankMinGames)
        {
            cards.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindGankDeaths,
                Title: "Dying to jungle ganks",
                Detail: $"{count} laning-phase deaths to the enemy jungler across {games} games "
                    + $"in the last {PatternConstants.WindowDays} days.",
                GameId: latestGameId,
                Severity: "high",
                MomentCount: count,
                GameCount: games));
        }
    }

    private static async Task AddLostObjectiveFightsCardAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        // Matches the materialized 'Lost Dragon/Baron/Herald fight' regions
        // (never 'Lost Teamfight' — no space before 'fight').
        var (count, games, latestGameId) = await CountWindowedTitleAsync(
            conn, windowStart, "LIKE", "Lost % fight%");
        if (count >= PatternConstants.LostFightMinCount && games >= PatternConstants.LostFightMinGames)
        {
            cards.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindLostObjectiveFights,
                Title: "Repeated lost objective fights",
                Detail: $"{count} lost fights at dragon, baron, or herald across {games} games "
                    + $"in the last {PatternConstants.WindowDays} days.",
                GameId: latestGameId,
                Severity: "high",
                MomentCount: count,
                GameCount: games));
        }
    }

    private static async Task AddDeathsBeforeObjectivesCardAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        var (count, games, latestGameId) = await CountWindowedTitleAsync(
            conn, windowStart, "LIKE", "Death before %");
        if (count >= PatternConstants.DeathBeforeObjMinCount && games >= PatternConstants.DeathBeforeObjMinGames)
        {
            cards.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindDeathsBeforeObjectives,
                Title: "Deaths before major objectives",
                Detail: $"{count} deaths 15-75s before dragon, baron, or herald across {games} games "
                    + $"in the last {PatternConstants.WindowDays} days.",
                GameId: latestGameId,
                Severity: "medium",
                MomentCount: count,
                GameCount: games));
        }
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
    /// A negative concept tag recurring across a meaningful share of the window's
    /// reviewed games. Counts the materialized per-game anchor rows, but requires
    /// the LIVE game_concept_tags row via EXISTS — untagging a game drops it from
    /// count and playlist symmetrically with no reconciliation pass.
    /// </summary>
    private static async Task AddRecurringConceptTagCardsAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        int reviewedGames;
        using (var denomCmd = conn.CreateCommand())
        {
            denomCmd.CommandText = $"""
                SELECT COUNT(*)
                FROM games g
                WHERE COALESCE(g.rating, 0) > 0
                  AND {PatternGamePredicate}
                """;
            denomCmd.Parameters.AddWithValue("@windowStart", windowStart);
            reviewedGames = Convert.ToInt32(await denomCmd.ExecuteScalarAsync() ?? 0);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT ct.id, ct.name, COUNT(*) AS cnt, MAX(e.game_id)
            FROM evidence_items e
            JOIN games g ON g.game_id = e.game_id
            JOIN concept_tags ct ON e.source_key = '{PatternConstants.TagSourceKeyPrefix}' || ct.id
                AND ct.polarity = 'negative'
            WHERE e.source_kind = '{EvidenceKinds.TimelineRegion}'
              AND e.status != 'dismissed'
              AND EXISTS (
                    SELECT 1 FROM game_concept_tags gct
                    WHERE gct.game_id = e.game_id AND gct.tag_id = ct.id)
              AND {PatternGamePredicate}
            GROUP BY ct.id
            HAVING cnt >= @minCount
            ORDER BY cnt DESC
            """;
        cmd.Parameters.AddWithValue("@minCount", PatternConstants.TagMinCount);
        cmd.Parameters.AddWithValue("@windowStart", windowStart);

        var added = 0;
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync() && added < PatternConstants.TagCardLimit)
        {
            var tagId = reader.GetInt64(0);
            var name = reader.GetString(1);
            var count = Convert.ToInt32(reader.GetInt64(2));
            var latestGameId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);

            var share = Math.Min(1.0, count / (double)Math.Max(1, reviewedGames));
            if (share < PatternConstants.TagMinShare)
            {
                continue;
            }

            cards.Add(new ObjectivePatternCard(
                Kind: PatternConstants.KindRecurringConceptTag,
                Title: $"Recurring: {name}",
                Detail: $"'{name}' tagged in {count} of {reviewedGames} reviewed games "
                    + $"in the last {PatternConstants.WindowDays} days.",
                GameId: latestGameId,
                Severity: share >= PatternConstants.TagHighShare ? "high" : "medium",
                Discriminator: $"tag{tagId}",
                MomentCount: count,
                GameCount: count));
            added++;
        }
    }

    /// <summary>Rule-broken games in the window (one anchor per game, gated on
    /// the LIVE session_log.rule_broken flag so a user-cleared false positive
    /// drops out of count and playlist).</summary>
    private static async Task AddRuleBreaksCardAsync(
        SqliteConnection conn, long windowStart, List<ObjectivePatternCard> cards)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), MAX(e.game_id)
            FROM evidence_items e
            JOIN games g ON g.game_id = e.game_id
            WHERE e.source_kind = '{EvidenceKinds.TimelineRegion}'
              AND e.source_key = '{PatternConstants.RuleBreakSourceKey}'
              AND e.status != 'dismissed'
              AND EXISTS (
                    SELECT 1 FROM session_log sl
                    WHERE sl.game_id = e.game_id AND COALESCE(sl.rule_broken, 0) = 1)
              AND {PatternGamePredicate}
            """;
        cmd.Parameters.AddWithValue("@windowStart", windowStart);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var count = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0));
            var latestGameId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            if (count >= PatternConstants.RuleBreakMinCount)
            {
                cards.Add(new ObjectivePatternCard(
                    Kind: PatternConstants.KindRuleBreaks,
                    Title: "Breaking your own queue rules",
                    Detail: $"You broke your own queue rules in {count} games "
                        + $"in the last {PatternConstants.WindowDays} days.",
                    GameId: latestGameId,
                    Severity: count >= PatternConstants.RuleBreakHighCount ? "high" : "medium",
                    MomentCount: count,
                    GameCount: count));
            }
        }
    }

    private static async Task<(int Count, int Games, long? LatestGameId)> CountWindowedTitleAsync(
        SqliteConnection conn, long windowStart, string comparison, string title)
    {
        // comparison is an internal constant ("=" or "LIKE"), never user input.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), COUNT(DISTINCT e.game_id), MAX(e.game_id)
            FROM evidence_items e
            JOIN games g ON g.game_id = e.game_id
            WHERE e.status != 'dismissed'
              AND e.title {comparison} @title
              AND {PatternGamePredicate}
            """;
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@windowStart", windowStart);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0)),
                reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : reader.GetInt64(2));
        }
        return (0, 0, null);
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
            case PatternConstants.KindDeathClassMix:
            {
                // Discriminator carries the class key; match the exact audit
                // title (NOT source_key) so a moment the note flow promoted to a
                // clip — which rewrites source_kind/source_key — stays listed.
                var label = DeathClasses.LabelFor(pattern.Discriminator);
                if (label.Length == 0) return Array.Empty<PatternMoment>();
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.status != 'dismissed'
                      AND e.title = @title
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                cmd.Parameters.AddWithValue("@title", PatternConstants.DeathAuditTitle(label));
                break;
            }

            case PatternConstants.KindGankDeaths:
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.status != 'dismissed'
                      AND e.title = @title
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                cmd.Parameters.AddWithValue("@title", PatternConstants.GankDeathTitle);
                break;

            case PatternConstants.KindLostObjectiveFights:
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.status != 'dismissed'
                      AND e.title LIKE 'Lost % fight%'
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                break;

            case PatternConstants.KindDeathsBeforeObjectives:
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.status != 'dismissed'
                      AND e.title LIKE 'Death before %'
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                break;

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

            case PatternConstants.KindRecurringConceptTag:
            {
                // Discriminator "tag{id}" → the anchor rows for that tag, gated
                // on the LIVE game_concept_tags row (untagged games vanish from
                // count and playlist symmetrically).
                if (!pattern.Discriminator.StartsWith("tag", StringComparison.Ordinal)
                    || !long.TryParse(pattern.Discriminator["tag".Length..], out var tagId))
                {
                    return Array.Empty<PatternMoment>();
                }
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.source_kind = '{EvidenceKinds.TimelineRegion}'
                      AND e.source_key = @sourceKey
                      AND e.status != 'dismissed'
                      AND EXISTS (
                            SELECT 1 FROM game_concept_tags gct
                            WHERE gct.game_id = e.game_id AND gct.tag_id = @tagId)
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                cmd.Parameters.AddWithValue("@sourceKey", PatternConstants.TagSourceKey(tagId));
                cmd.Parameters.AddWithValue("@tagId", tagId);
                break;
            }

            case PatternConstants.KindRuleBreaks:
                cmd.CommandText = $"""
                    {SelectPatternMomentSql}
                    WHERE e.source_kind = '{EvidenceKinds.TimelineRegion}'
                      AND e.source_key = '{PatternConstants.RuleBreakSourceKey}'
                      AND e.status != 'dismissed'
                      AND EXISTS (
                            SELECT 1 FROM session_log sl
                            WHERE sl.game_id = e.game_id AND COALESCE(sl.rule_broken, 0) = 1)
                      AND {PatternGamePredicate}
                    {PatternMomentOrderBy}
                    """;
                break;

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
    /// Find a death-audit moment the note flow promoted to a clip:
    /// AttachClipToEvidenceAsync rewrote source_kind/source_key (so the
    /// death-audit source key no longer matches) but preserved the title AND the
    /// moment's exact window (the note endpoint clips a real start/end range
    /// verbatim). The EXACT window match is the row's identity — containment
    /// matching could adopt a neighbouring death's clip when two deaths fall
    /// within one 14s window. Also matches a promoted row a later CLEAR retitled
    /// to the plain cleared title, so re-classifying retitles in place.
    /// </summary>
    public async Task<long?> FindPromotedDeathAuditAsync(long gameId, int gameTimeSeconds)
    {
        var startS = Math.Max(0, gameTimeSeconds - PatternConstants.DeathMomentLeadSeconds);
        var endS = gameTimeSeconds + PatternConstants.DeathMomentTrailSeconds;
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.id
            FROM evidence_items e
            WHERE e.game_id = @gameId
              AND e.source_kind = '{EvidenceKinds.Clip}'
              AND (e.title LIKE @auditPrefix OR e.title = @clearedTitle)
              AND e.start_time_s = @startS
              AND e.end_time_s = @endS
            ORDER BY e.id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@auditPrefix", PatternConstants.DeathAuditTitlePrefix + "%");
        cmd.Parameters.AddWithValue("@clearedTitle", PatternConstants.ClearedDeathAuditTitle);
        cmd.Parameters.AddWithValue("@startS", startS);
        cmd.Parameters.AddWithValue("@endS", endS);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

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

    public async Task UpdateTitleAsync(long evidenceId, string title)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE evidence_items
            SET title = @title,
                updated_at = @updatedAt
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@title", title ?? "");
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", evidenceId);
        await cmd.ExecuteNonQueryAsync();
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

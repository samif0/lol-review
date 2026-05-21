#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

public sealed class EvidenceRepository : IEvidenceRepository
{
    private readonly IDbConnectionFactory _factory;

    private const string UnreviewedEvidenceGamePredicate = """
        COALESCE(g.rating, 0) <= 0
        AND COALESCE(g.review_notes, '') = ''
        AND COALESCE(g.mistakes, '') = ''
        AND COALESCE(g.went_well, '') = ''
        AND COALESCE(g.focus_next, '') = ''
        AND COALESCE(g.spotted_problems, '') = ''
        AND COALESCE(g.outside_control, '') = ''
        AND COALESCE(g.within_control, '') = ''
        AND COALESCE(g.attribution, '') = ''
        AND COALESCE(g.personal_contribution, '') = ''
        AND COALESCE(sl.improvement_note, '') = ''
        AND COALESCE(sl.mental_handled, '') = ''
        AND COALESCE(sl.is_skipped, 0) = 0
        """;

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

        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            using var findCmd = conn.CreateCommand();
            findCmd.CommandText = """
                SELECT id
                FROM evidence_items
                WHERE game_id = @gameId
                  AND source_kind = @sourceKind
                  AND source_key = @sourceKey
                LIMIT 1
                """;
            findCmd.Parameters.AddWithValue("@gameId", item.GameId);
            findCmd.Parameters.AddWithValue("@sourceKind", sourceKind);
            findCmd.Parameters.AddWithValue("@sourceKey", sourceKey);
            var found = await findCmd.ExecuteScalarAsync();
            if (found is not null and not DBNull)
            {
                existingId = Convert.ToInt64(found);
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
            return id;
        }

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO evidence_items
                (game_id, source_kind, source_id, source_key, start_time_s, end_time_s,
                 title, note, objective_id, concept_tag_id, matchup_note_id,
                 polarity, status, created_at, updated_at)
            VALUES
                (@gameId, @sourceKind, @sourceId, @sourceKey, @startTimeS, @endTimeS,
                 @title, @note, @objectiveId, @conceptTagId, @matchupNoteId,
                 @polarity, @status, @createdAt, @updatedAt)
            """;
        BindUpsert(insertCmd, item, sourceKind, sourceKey, polarity, status, now);
        insertCmd.Parameters.AddWithValue("@createdAt", now);
        await insertCmd.ExecuteNonQueryAsync();

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

    public async Task UpdateObjectiveAsync(long evidenceId, long? objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
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

    public async Task<IReadOnlyList<ObjectivePatternCard>> GetPatternCardsAsync(int limit = 6)
    {
        var cards = new List<ObjectivePatternCard>();
        using var conn = _factory.CreateConnection();

        var isolatedDeathPattern = await CountTitleLikeAsync(conn, "%Isolated death%");
        if (isolatedDeathPattern.Count >= 3)
        {
            cards.Add(new ObjectivePatternCard(
                Kind: "isolated_deaths",
                Title: "Frequent isolated deaths",
                Detail: $"{isolatedDeathPattern.Count} isolated-death moments are waiting in the evidence ledger.",
                GameId: isolatedDeathPattern.LatestGameId,
                Severity: "high"));
        }

        var lostObjectiveFightPattern = await CountTitleLikeAsync(conn, "Lost % fight%");
        if (lostObjectiveFightPattern.Count >= 2)
        {
            cards.Add(new ObjectivePatternCard(
                Kind: "lost_objective_fights",
                Title: "Repeated lost objective fights",
                Detail: $"{lostObjectiveFightPattern.Count} lost objective-fight moments are tagged or pending.",
                GameId: lostObjectiveFightPattern.LatestGameId,
                Severity: "high"));
        }

        var deathsBeforeObjectives = await CountTitleLikeAsync(conn, "Death before %");
        if (deathsBeforeObjectives.Count >= 2)
        {
            cards.Add(new ObjectivePatternCard(
                Kind: "deaths_before_objectives",
                Title: "Deaths before major objectives",
                Detail: $"{deathsBeforeObjectives.Count} deaths happened shortly before dragon, baron, or herald moments.",
                GameId: deathsBeforeObjectives.LatestGameId,
                Severity: "high"));
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT e.objective_id,
                       COALESCE(o.title, '') AS objective_title,
                       SUM(CASE WHEN e.polarity = 'bad' THEN 1 ELSE 0 END) AS bad_count,
                       SUM(CASE WHEN e.polarity = 'good' THEN 1 ELSE 0 END) AS good_count,
                       COUNT(*) AS total_count
                FROM evidence_items e
                LEFT JOIN objectives o ON o.id = e.objective_id
                LEFT JOIN games g ON g.game_id = e.game_id
                LEFT JOIN session_log sl ON sl.game_id = e.game_id
                WHERE e.objective_id IS NOT NULL
                  AND e.status != 'dismissed'
                  AND {UnreviewedEvidenceGamePredicate}
                GROUP BY e.objective_id
                HAVING bad_count >= 2 AND bad_count > good_count
                ORDER BY bad_count DESC, total_count DESC
                LIMIT 3
                """;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var objectiveId = reader.GetInt64(0);
                var title = reader.IsDBNull(1) ? "Objective" : reader.GetString(1);
                var badCount = Convert.ToInt32(reader.GetInt64(2));
                var total = Convert.ToInt32(reader.GetInt64(4));
                cards.Add(new ObjectivePatternCard(
                    Kind: "bad_objective_evidence",
                    Title: $"{title}: mostly bad examples",
                    Detail: $"{badCount} of {total} linked evidence items are marked bad.",
                    ObjectiveId: objectiveId,
                    Severity: "medium"));
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT e.game_id, COUNT(*) AS bad_count
                FROM evidence_items e
                LEFT JOIN games g ON g.game_id = e.game_id
                LEFT JOIN session_log sl ON sl.game_id = e.game_id
                WHERE e.matchup_note_id IS NOT NULL
                  AND e.polarity = 'bad'
                  AND e.status != 'dismissed'
                  AND {UnreviewedEvidenceGamePredicate}
                GROUP BY e.game_id
                HAVING bad_count >= 2
                ORDER BY bad_count DESC
                LIMIT 2
                """;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                cards.Add(new ObjectivePatternCard(
                    Kind: "negative_matchup_clips",
                    Title: "Matchup notes have negative clips",
                    Detail: $"{Convert.ToInt32(reader.GetInt64(1))} bad examples are attached to matchup evidence.",
                    GameId: reader.GetInt64(0),
                    Severity: "medium"));
            }
        }

        return cards.Take(Math.Max(1, limit)).ToArray();
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

    private static async Task<(int Count, long? LatestGameId)> CountTitleLikeAsync(SqliteConnection conn, string pattern)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), MAX(e.game_id)
            FROM evidence_items e
            LEFT JOIN games g ON g.game_id = e.game_id
            LEFT JOIN session_log sl ON sl.game_id = e.game_id
            WHERE e.status != 'dismissed'
              AND e.title LIKE @pattern
              AND {UnreviewedEvidenceGamePredicate}
            """;
        cmd.Parameters.AddWithValue("@pattern", pattern);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var latestGameId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            return (reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0)), latestGameId);
        }

        return (0, null);
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
               g.timestamp
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
                GameTimestamp: reader.IsDBNull(20) ? null : reader.GetInt64(20)));
        }

        return rows;
    }
}

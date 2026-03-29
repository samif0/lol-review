#nullable enable

using System.Diagnostics;
using System.Text.Json;
using LoLReview.Core.Data;
using LoLReview.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

public sealed class CoachLabService : ICoachLabService
{
    private const int CoachArtifactVersion = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IClipService _clipService;
    private readonly ICoachSidecarClient _sidecarClient;
    private readonly ICoachRecommendationService _recommendationService;
    private readonly ICoachTrainingService _trainingService;
    private readonly ILogger<CoachLabService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public CoachLabService(
        IDbConnectionFactory connectionFactory,
        IClipService clipService,
        ICoachSidecarClient sidecarClient,
        ICoachRecommendationService recommendationService,
        ICoachTrainingService trainingService,
        ILogger<CoachLabService> logger)
    {
        _connectionFactory = connectionFactory;
        _clipService = clipService;
        _sidecarClient = sidecarClient;
        _recommendationService = recommendationService;
        _trainingService = trainingService;
        _logger = logger;
    }

    public bool IsEnabled => CoachLabFeature.IsEnabled();

    public async Task<CoachDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new CoachDashboardSnapshot { IsEnabled = false };
        }

        using var connection = _connectionFactory.CreateConnection();
        var bootstrap = await EnsureBootstrapStateAsync(connection, cancellationToken);
        await UpdateDatasetVersionAsync(connection, bootstrap.Player.Id, cancellationToken);

        var counts = await GetDatasetCountsAsync(connection, cancellationToken);
        var latestRecommendation = await GetLatestRecommendationAsync(connection, bootstrap.Block.Id, cancellationToken);
        var trainingStatus = await _trainingService.GetStatusAsync(cancellationToken);

        return new CoachDashboardSnapshot
        {
            IsEnabled = true,
            IsAssistMode = true,
            ActiveObjectiveTitle = bootstrap.Block.ObjectiveTitle,
            ActiveObjectiveKey = bootstrap.Block.ObjectiveKey,
            RecommendationTitle = latestRecommendation?.Title ?? "Assist mode active",
            RecommendationSummary = latestRecommendation?.Summary ?? "Collect clip-backed evidence before promoting stronger coaching behavior.",
            WatchItemTitle = BuildWatchItemTitle(latestRecommendation),
            WatchItemSummary = BuildWatchItemSummary(latestRecommendation),
            TotalMoments = counts.Total,
            GoldMoments = counts.Gold,
            SilverMoments = counts.Silver,
            BronzeMoments = counts.Bronze,
            PendingMoments = counts.Pending,
            ReviewedGames = counts.ReviewedGames,
            DatasetVersion = "bootstrap-v1",
            ActiveModelVersion = trainingStatus.ActiveModelVersion,
            TrainingStatus = trainingStatus,
        };
    }

    public async Task<IReadOnlyList<CoachMomentCard>> GetMomentQueueAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return [];
        }

        using var connection = _connectionFactory.CreateConnection();
        await EnsureBootstrapStateAsync(connection, cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                m.id,
                m.game_id,
                m.bookmark_id,
                m.source_type,
                m.champion,
                m.role,
                m.game_time_s,
                m.clip_path,
                m.storyboard_path,
                m.hud_strip_path,
                m.minimap_strip_path,
                m.manifest_path,
                m.note_text,
                m.context_text,
                COALESCE(i.moment_quality, 'neutral'),
                COALESCE(i.primary_reason, ''),
                COALESCE(i.objective_key, ''),
                i.attached_objective_id,
                COALESCE(i.attached_objective_title, ''),
                COALESCE(i.confidence, 0),
                COALESCE(i.rationale, ''),
                COALESCE(l.label_quality, ''),
                COALESCE(l.primary_reason, ''),
                COALESCE(l.objective_key, ''),
                l.attached_objective_id,
                COALESCE(l.attached_objective_title, ''),
                COALESCE(l.explanation, ''),
                COALESCE(l.confidence, 0),
                b.objective_id,
                COALESCE(b.objective_title, ''),
                m.created_at,
                m.reviewed_at
            FROM coach_moments m
            LEFT JOIN coach_inferences i ON i.moment_id = m.id
            LEFT JOIN coach_labels l ON l.moment_id = m.id
            LEFT JOIN coach_objective_blocks b ON b.id = m.objective_block_id
            ORDER BY
                CASE WHEN COALESCE(l.label_quality, '') = '' THEN 0 ELSE 1 END ASC,
                CASE WHEN m.source_type = 'manual_clip' THEN 0 ELSE 1 END ASC,
                m.created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<CoachMomentCard>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadMomentCard(reader));
        }

        return results;
    }

    public async Task<CoachMomentCard?> GetMomentAsync(long momentId, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        using var connection = _connectionFactory.CreateConnection();
        await EnsureBootstrapStateAsync(connection, cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                m.id,
                m.game_id,
                m.bookmark_id,
                m.source_type,
                m.champion,
                m.role,
                m.game_time_s,
                m.clip_path,
                m.storyboard_path,
                m.hud_strip_path,
                m.minimap_strip_path,
                m.manifest_path,
                m.note_text,
                m.context_text,
                COALESCE(i.moment_quality, 'neutral'),
                COALESCE(i.primary_reason, ''),
                COALESCE(i.objective_key, ''),
                i.attached_objective_id,
                COALESCE(i.attached_objective_title, ''),
                COALESCE(i.confidence, 0),
                COALESCE(i.rationale, ''),
                COALESCE(l.label_quality, ''),
                COALESCE(l.primary_reason, ''),
                COALESCE(l.objective_key, ''),
                l.attached_objective_id,
                COALESCE(l.attached_objective_title, ''),
                COALESCE(l.explanation, ''),
                COALESCE(l.confidence, 0),
                b.objective_id,
                COALESCE(b.objective_title, ''),
                m.created_at,
                m.reviewed_at
            FROM coach_moments m
            LEFT JOIN coach_inferences i ON i.moment_id = m.id
            LEFT JOIN coach_labels l ON l.moment_id = m.id
            LEFT JOIN coach_objective_blocks b ON b.id = m.objective_block_id
            WHERE m.id = @id
            """;
        cmd.Parameters.AddWithValue("@id", momentId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMomentCard(reader)
            : null;
    }

    public async Task<CoachSyncResult> SyncMomentsAsync(
        bool includeAutoSamples = true,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new CoachSyncResult();
        }

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory.CreateConnection();
            var bootstrap = await EnsureBootstrapStateAsync(connection, cancellationToken);

            var manualImported = await SyncManualClipMomentsAsync(connection, bootstrap, cancellationToken);
            var autoCreated = includeAutoSamples
                ? await SyncAutoSampleMomentsAsync(connection, bootstrap, cancellationToken)
                : 0;
            var draftsCreated = await EnsureDraftsAsync(connection, bootstrap.Player.Id, cancellationToken);
            await RefreshArtifactsIfNeededAsync(connection, cancellationToken);

            await UpdateDatasetVersionAsync(connection, bootstrap.Player.Id, cancellationToken);

            return new CoachSyncResult
            {
                ManualClipsImported = manualImported,
                AutoSamplesCreated = autoCreated,
                DraftsCreated = draftsCreated,
            };
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SaveManualLabelAsync(
        long momentId,
        CoachManualLabelInput input,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        using var connection = _connectionFactory.CreateConnection();
        var bootstrap = await EnsureBootstrapStateAsync(connection, cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var momentContext = await LoadMomentContextForLabelAsync(connection, momentId, cancellationToken);
        var attachedObjectiveTitle = await LookupObjectiveTitleAsync(connection, input.AttachedObjectiveId, cancellationToken);
        var inferredPrimaryReason = await InferPrimaryReasonAsync(momentContext, attachedObjectiveTitle, input, cancellationToken);
        var inferredObjectiveKey = !string.IsNullOrWhiteSpace(input.ObjectiveKey)
            ? input.ObjectiveKey.Trim()
            : CoachObjectiveCatalog.Find(inferredPrimaryReason)?.Key
                ?? CoachDraftHeuristics.InferObjectiveKey(
                    attachedObjectiveTitle,
                    momentContext.ActiveObjectiveTitle,
                    momentContext.NoteText,
                    momentContext.ContextText,
                    input.Explanation);

        using var tx = connection.BeginTransaction();

        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM coach_labels WHERE moment_id = @momentId";
            deleteCmd.Parameters.AddWithValue("@momentId", momentId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO coach_labels
                    (moment_id, player_id, label_quality, primary_reason, objective_key, attached_objective_id, attached_objective_title, explanation, confidence, source, created_at, updated_at)
                VALUES
                    (@momentId, @playerId, @labelQuality, @primaryReason, @objectiveKey, @attachedObjectiveId, @attachedObjectiveTitle, @explanation, @confidence, 'manual', @createdAt, @updatedAt)
                """;
            insertCmd.Parameters.AddWithValue("@momentId", momentId);
            insertCmd.Parameters.AddWithValue("@playerId", bootstrap.Player.Id);
            insertCmd.Parameters.AddWithValue("@labelQuality", input.LabelQuality.Trim().ToLowerInvariant());
            insertCmd.Parameters.AddWithValue("@primaryReason", inferredPrimaryReason);
            insertCmd.Parameters.AddWithValue("@objectiveKey", inferredObjectiveKey);
            insertCmd.Parameters.AddWithValue("@attachedObjectiveId", input.AttachedObjectiveId.HasValue ? input.AttachedObjectiveId.Value : DBNull.Value);
            insertCmd.Parameters.AddWithValue("@attachedObjectiveTitle", attachedObjectiveTitle);
            insertCmd.Parameters.AddWithValue("@explanation", input.Explanation.Trim());
            insertCmd.Parameters.AddWithValue("@confidence", Math.Clamp(input.Confidence, 0.2, 1.0));
            insertCmd.Parameters.AddWithValue("@createdAt", now);
            insertCmd.Parameters.AddWithValue("@updatedAt", now);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var reviewCmd = connection.CreateCommand())
        {
            reviewCmd.Transaction = tx;
            reviewCmd.CommandText = "UPDATE coach_moments SET reviewed_at = @reviewedAt WHERE id = @momentId";
            reviewCmd.Parameters.AddWithValue("@reviewedAt", now);
            reviewCmd.Parameters.AddWithValue("@momentId", momentId);
            await reviewCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        await UpdateDatasetVersionAsync(connection, bootstrap.Player.Id, cancellationToken);
    }

    public async Task<int> RedraftPendingMomentsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return 0;
        }

        using var connection = _connectionFactory.CreateConnection();
        var bootstrap = await EnsureBootstrapStateAsync(connection, cancellationToken);

        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = """
                DELETE FROM coach_inferences
                WHERE moment_id IN (
                    SELECT m.id
                    FROM coach_moments m
                    LEFT JOIN coach_labels l ON l.moment_id = m.id
                    WHERE m.player_id = @playerId
                      AND COALESCE(l.label_quality, '') = ''
                )
                """;
            deleteCmd.Parameters.AddWithValue("@playerId", bootstrap.Player.Id);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return await EnsureDraftsAsync(connection, bootstrap.Player.Id, cancellationToken);
    }

    public async Task<int> RedraftAllMomentsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return 0;
        }

        using var connection = _connectionFactory.CreateConnection();
        var bootstrap = await EnsureBootstrapStateAsync(connection, cancellationToken);

        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = """
                DELETE FROM coach_inferences
                WHERE moment_id IN (
                    SELECT id
                    FROM coach_moments
                    WHERE player_id = @playerId
                )
                """;
            deleteCmd.Parameters.AddWithValue("@playerId", bootstrap.Player.Id);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return await EnsureDraftsAsync(connection, bootstrap.Player.Id, cancellationToken);
    }

    public async Task<CoachRecommendation?> RefreshRecommendationAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        using var connection = _connectionFactory.CreateConnection();
        var bootstrap = await EnsureBootstrapStateAsync(connection, cancellationToken);
        var recommendation = await _recommendationService.BuildAssistRecommendationAsync(
            bootstrap.Player.Id,
            bootstrap.Block,
            cancellationToken);

        using var tx = connection.BeginTransaction();
        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = """
                DELETE FROM coach_recommendations
                WHERE objective_block_id = @blockId
                """;
            deleteCmd.Parameters.AddWithValue("@blockId", bootstrap.Block.Id);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO coach_recommendations
                    (objective_block_id, player_id, recommendation_type, state, objective_key, title, summary, confidence, evidence_game_count, raw_payload, created_at, updated_at)
                VALUES
                    (@objectiveBlockId, @playerId, @recommendationType, @state, @objectiveKey, @title, @summary, @confidence, @evidenceGameCount, @rawPayload, @createdAt, @updatedAt)
                """;
            insertCmd.Parameters.AddWithValue("@objectiveBlockId", recommendation.ObjectiveBlockId);
            insertCmd.Parameters.AddWithValue("@playerId", recommendation.PlayerId);
            insertCmd.Parameters.AddWithValue("@recommendationType", recommendation.RecommendationType);
            insertCmd.Parameters.AddWithValue("@state", recommendation.State);
            insertCmd.Parameters.AddWithValue("@objectiveKey", recommendation.ObjectiveKey);
            insertCmd.Parameters.AddWithValue("@title", recommendation.Title);
            insertCmd.Parameters.AddWithValue("@summary", recommendation.Summary);
            insertCmd.Parameters.AddWithValue("@confidence", recommendation.Confidence);
            insertCmd.Parameters.AddWithValue("@evidenceGameCount", recommendation.EvidenceGameCount);
            insertCmd.Parameters.AddWithValue("@rawPayload", recommendation.RawPayload);
            insertCmd.Parameters.AddWithValue("@createdAt", recommendation.CreatedAt);
            insertCmd.Parameters.AddWithValue("@updatedAt", recommendation.UpdatedAt);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return recommendation;
    }

    public async Task<CoachProblemsReport> GetModelProblemsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new CoachProblemsReport
            {
                Title = "Coach Lab disabled",
                Summary = "Coach Lab is hidden on this install.",
                UsesTrainedModel = false,
            };
        }

        using var connection = _connectionFactory.CreateConnection();
        var bootstrap = await EnsureBootstrapStateAsync(connection, cancellationToken);
        return await _recommendationService.BuildProblemsReportAsync(
            bootstrap.Player.Id,
            bootstrap.Block,
            cancellationToken);
    }

    public async Task<CoachObjectiveSuggestion> GenerateObjectiveSuggestionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new CoachObjectiveSuggestion
            {
                Title = "Coach Lab disabled",
                Summary = "Coach Lab is hidden on this install.",
                UsesTrainedModel = false,
            };
        }

        using var connection = _connectionFactory.CreateConnection();
        var bootstrap = await EnsureBootstrapStateAsync(connection, cancellationToken);
        return await _recommendationService.GenerateObjectiveSuggestionAsync(
            bootstrap.Player.Id,
            bootstrap.Block,
            cancellationToken);
    }

    private async Task<(CoachPlayer Player, CoachObjectiveBlock Block)> EnsureBootstrapStateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var player = await EnsurePrimaryPlayerAsync(connection, cancellationToken);
        await EnsureActiveModelAsync(connection, cancellationToken);
        await EnsureActiveDatasetVersionAsync(connection, cancellationToken);
        var block = await EnsureActiveObjectiveBlockAsync(connection, player.Id, cancellationToken);
        return (player, block);
    }

    private async Task<CoachPlayer> EnsurePrimaryPlayerAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = """
                SELECT id, display_name, is_primary, created_at, updated_at
                FROM coach_players
                WHERE is_primary = 1
                ORDER BY id ASC
                LIMIT 1
                """;
            using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new CoachPlayer
                {
                    Id = reader.GetInt64(0),
                    DisplayName = reader.IsDBNull(1) ? "Primary Player" : reader.GetString(1),
                    IsPrimary = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                    CreatedAt = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    UpdatedAt = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                };
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO coach_players (display_name, is_primary, created_at, updated_at)
                VALUES ('Primary Player', 1, @createdAt, @updatedAt)
                """;
            insertCmd.Parameters.AddWithValue("@createdAt", now);
            insertCmd.Parameters.AddWithValue("@updatedAt", now);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        using var idCmd = connection.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        var id = (long)(await idCmd.ExecuteScalarAsync(cancellationToken))!;

        return new CoachPlayer
        {
            Id = id,
            DisplayName = "Primary Player",
            IsPrimary = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private async Task EnsureActiveModelAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO coach_models
                (model_version, model_kind, display_name, provider, is_active, metadata_json, created_at)
            VALUES
                ('assist-heuristic-v1', 'assist', 'Assist Heuristic v1', 'local', 1, '{"mode":"assist"}', @createdAt)
            """;
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureActiveDatasetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO coach_dataset_versions
                (dataset_version, status, gold_count, silver_count, bronze_count, reviewed_games, created_at, updated_at)
            VALUES
                ('bootstrap-v1', 'active', 0, 0, 0, 0, @createdAt, @updatedAt)
            """;
        cmd.Parameters.AddWithValue("@createdAt", now);
        cmd.Parameters.AddWithValue("@updatedAt", now);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<CoachObjectiveBlock> EnsureActiveObjectiveBlockAsync(
        SqliteConnection connection,
        long playerId,
        CancellationToken cancellationToken)
    {
        var activeObjective = await GetCurrentObjectiveAsync(connection, cancellationToken);

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, player_id, objective_id, objective_title, objective_key, status, mode, started_at, updated_at, completed_at, notes
                FROM coach_objective_blocks
                WHERE player_id = @playerId AND status = 'active'
                ORDER BY started_at DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@playerId", playerId);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var block = ReadObjectiveBlock(reader);
                if (NeedsObjectiveBlockRotation(block, activeObjective))
                {
                    await CompleteObjectiveBlockAsync(connection, block.Id, cancellationToken);
                }
                else
                {
                    return block;
                }
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var objectiveKey = CoachDraftHeuristics.InferObjectiveKey(activeObjective.Title);

        using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO coach_objective_blocks
                    (player_id, objective_id, objective_title, objective_key, status, mode, started_at, updated_at, notes)
                VALUES
                    (@playerId, @objectiveId, @objectiveTitle, @objectiveKey, 'active', 'assist', @startedAt, @updatedAt, @notes)
                """;
            insertCmd.Parameters.AddWithValue("@playerId", playerId);
            insertCmd.Parameters.AddWithValue("@objectiveId", activeObjective.Id.HasValue ? activeObjective.Id.Value : DBNull.Value);
            insertCmd.Parameters.AddWithValue("@objectiveTitle", activeObjective.Title);
            insertCmd.Parameters.AddWithValue("@objectiveKey", objectiveKey);
            insertCmd.Parameters.AddWithValue("@startedAt", now);
            insertCmd.Parameters.AddWithValue("@updatedAt", now);
            insertCmd.Parameters.AddWithValue("@notes", activeObjective.Title == "Observe lane phase"
                ? "Bootstrap block created without an active objective."
                : "Bootstrap block mirrored from the active objective.");
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        using var idCmd = connection.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        var id = (long)(await idCmd.ExecuteScalarAsync(cancellationToken))!;

        return new CoachObjectiveBlock
        {
            Id = id,
            PlayerId = playerId,
            ObjectiveId = activeObjective.Id,
            ObjectiveTitle = activeObjective.Title,
            ObjectiveKey = objectiveKey,
            Status = "active",
            Mode = "assist",
            StartedAt = now,
            UpdatedAt = now,
        };
    }

    private static bool NeedsObjectiveBlockRotation(CoachObjectiveBlock block, (long? Id, string Title) activeObjective)
    {
        if (activeObjective.Id is null)
        {
            return false;
        }

        if (block.ObjectiveId == activeObjective.Id)
        {
            return false;
        }

        return !string.Equals(block.ObjectiveTitle, activeObjective.Title, StringComparison.OrdinalIgnoreCase);
    }

    private async Task CompleteObjectiveBlockAsync(SqliteConnection connection, long blockId, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE coach_objective_blocks
            SET status = 'superseded', completed_at = @completedAt, updated_at = @updatedAt
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@completedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", blockId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(long? Id, string Title)> GetCurrentObjectiveAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, title
            FROM objectives
            WHERE status = 'active'
            ORDER BY is_priority DESC, created_at ASC, id ASC
            LIMIT 1
            """;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1));
        }

        return (null, "Observe lane phase");
    }

    private async Task<int> SyncManualClipMomentsAsync(
        SqliteConnection connection,
        (CoachPlayer Player, CoachObjectiveBlock Block) bootstrap,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                b.id,
                b.game_id,
                b.game_time_s,
                b.note,
                b.clip_start_s,
                b.clip_end_s,
                b.clip_path,
                COALESCE(g.champion_name, ''),
                COALESCE(NULLIF(g.role, ''), g.position, ''),
                COALESCE(g.review_notes, ''),
                COALESCE(g.mistakes, ''),
                COALESCE(g.focus_next, ''),
                COALESCE(g.went_well, ''),
                COALESCE(g.spotted_problems, '')
            FROM vod_bookmarks b
            INNER JOIN games g ON g.game_id = b.game_id
            LEFT JOIN coach_moments m ON m.bookmark_id = b.id
            WHERE COALESCE(TRIM(b.clip_path), '') <> ''
              AND m.id IS NULL
            ORDER BY b.created_at DESC, b.id DESC
            """;

        var imported = 0;
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var clipPath = reader.IsDBNull(6) ? "" : reader.GetString(6);
            if (!File.Exists(clipPath))
            {
                _logger.LogWarning("Skipping coach moment for missing clip {Path}", clipPath);
                continue;
            }

            var payload = new PendingMoment
            {
                PlayerId = bootstrap.Player.Id,
                ObjectiveBlockId = bootstrap.Block.Id,
                GameId = reader.GetInt64(1),
                BookmarkId = reader.GetInt64(0),
                SourceType = "manual_clip",
                Champion = reader.IsDBNull(7) ? "" : reader.GetString(7),
                Role = reader.IsDBNull(8) ? "" : reader.GetString(8),
                GameTimeS = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                ClipStartS = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                ClipEndS = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                ClipPath = clipPath,
                NoteText = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ContextText = BuildContextText(
                    reader.IsDBNull(9) ? "" : reader.GetString(9),
                    reader.IsDBNull(10) ? "" : reader.GetString(10),
                    reader.IsDBNull(11) ? "" : reader.GetString(11),
                    reader.IsDBNull(12) ? "" : reader.GetString(12),
                    reader.IsDBNull(13) ? "" : reader.GetString(13)),
            };

            await CreateMomentAsync(connection, payload, bootstrap.Block, cancellationToken);
            imported++;
        }

        return imported;
    }

    private async Task<int> SyncAutoSampleMomentsAsync(
        SqliteConnection connection,
        (CoachPlayer Player, CoachObjectiveBlock Block) bootstrap,
        CancellationToken cancellationToken)
    {
        var ffmpeg = await _clipService.FindFfmpegAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogInformation("Skipping auto-sampling because ffmpeg is not available.");
            return 0;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                g.game_id,
                g.game_duration,
                COALESCE(g.champion_name, ''),
                COALESCE(NULLIF(g.role, ''), g.position, ''),
                COALESCE(v.file_path, ''),
                COALESCE(g.review_notes, ''),
                COALESCE(g.mistakes, ''),
                COALESCE(g.focus_next, ''),
                COALESCE(g.went_well, ''),
                COALESCE(g.spotted_problems, '')
            FROM games g
            INNER JOIN vod_files v ON v.game_id = g.game_id
            WHERE (
                lower(COALESCE(g.role, '')) IN ('carry', 'adc', 'bot', 'bottom')
                OR lower(COALESCE(g.position, '')) IN ('carry', 'bot', 'bottom')
            )
            ORDER BY COALESCE(g.timestamp, 0) DESC
            LIMIT 24
            """;

        var created = 0;
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var gameId = reader.GetInt64(0);
            var duration = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var champion = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var role = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var vodPath = reader.IsDBNull(4) ? "" : reader.GetString(4);

            if (!File.Exists(vodPath))
            {
                continue;
            }

            foreach (var sampleSecond in GetAutoSampleSeconds(duration))
            {
                if (created >= 12)
                {
                    return created;
                }

                var clipStart = Math.Max(0, sampleSecond - 2);
                var clipEnd = sampleSecond + 2;
                if (await MomentExistsAsync(connection, gameId, "auto_sample", sampleSecond, clipStart, clipEnd, cancellationToken))
                {
                    continue;
                }

                var clipPath = await _clipService.ExtractClipAsync(
                    vodPath,
                    clipStart,
                    clipEnd,
                    champion,
                    Path.Combine(AppDataPaths.CoachAnalysisDirectory, "auto-samples"));

                if (string.IsNullOrWhiteSpace(clipPath))
                {
                    continue;
                }

                var payload = new PendingMoment
                {
                    PlayerId = bootstrap.Player.Id,
                    ObjectiveBlockId = bootstrap.Block.Id,
                    GameId = gameId,
                    BookmarkId = null,
                    SourceType = "auto_sample",
                    Champion = champion,
                    Role = role,
                    GameTimeS = sampleSecond,
                    ClipStartS = clipStart,
                    ClipEndS = clipEnd,
                    ClipPath = clipPath,
                    NoteText = "",
                    ContextText = BuildContextText(
                        reader.IsDBNull(5) ? "" : reader.GetString(5),
                        reader.IsDBNull(6) ? "" : reader.GetString(6),
                        reader.IsDBNull(7) ? "" : reader.GetString(7),
                        reader.IsDBNull(8) ? "" : reader.GetString(8),
                        reader.IsDBNull(9) ? "" : reader.GetString(9)),
                };

                await CreateMomentAsync(connection, payload, bootstrap.Block, cancellationToken);
                created++;
            }
        }

        return created;
    }

    private async Task<bool> MomentExistsAsync(
        SqliteConnection connection,
        long gameId,
        string sourceType,
        int gameTimeS,
        int clipStartS,
        int clipEndS,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM coach_moments
            WHERE game_id = @gameId
              AND source_type = @sourceType
              AND game_time_s = @gameTimeS
              AND COALESCE(clip_start_s, -1) = @clipStartS
              AND COALESCE(clip_end_s, -1) = @clipEndS
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@sourceType", sourceType);
        cmd.Parameters.AddWithValue("@gameTimeS", gameTimeS);
        cmd.Parameters.AddWithValue("@clipStartS", clipStartS);
        cmd.Parameters.AddWithValue("@clipEndS", clipEndS);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private async Task CreateMomentAsync(
        SqliteConnection connection,
        PendingMoment payload,
        CoachObjectiveBlock block,
        CancellationToken cancellationToken)
    {
        var artifacts = await BuildArtifactsAsync(payload, cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO coach_moments
                    (player_id, game_id, bookmark_id, objective_block_id, source_type, patch_version, champion, role, game_time_s, clip_start_s, clip_end_s, clip_path, storyboard_path, hud_strip_path, minimap_strip_path, manifest_path, note_text, context_text, dataset_version, model_version, created_at)
                VALUES
                    (@playerId, @gameId, @bookmarkId, @objectiveBlockId, @sourceType, 'unknown', @champion, @role, @gameTimeS, @clipStartS, @clipEndS, @clipPath, @storyboardPath, @hudStripPath, @minimapStripPath, @manifestPath, @noteText, @contextText, 'bootstrap-v1', 'assist-heuristic-v1', @createdAt)
                """;
            cmd.Parameters.AddWithValue("@playerId", payload.PlayerId);
            cmd.Parameters.AddWithValue("@gameId", payload.GameId);
            cmd.Parameters.AddWithValue("@bookmarkId", payload.BookmarkId.HasValue ? payload.BookmarkId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@objectiveBlockId", payload.ObjectiveBlockId.HasValue ? payload.ObjectiveBlockId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@sourceType", payload.SourceType);
            cmd.Parameters.AddWithValue("@champion", payload.Champion);
            cmd.Parameters.AddWithValue("@role", payload.Role);
            cmd.Parameters.AddWithValue("@gameTimeS", payload.GameTimeS);
            cmd.Parameters.AddWithValue("@clipStartS", payload.ClipStartS.HasValue ? payload.ClipStartS.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@clipEndS", payload.ClipEndS.HasValue ? payload.ClipEndS.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@clipPath", payload.ClipPath);
            cmd.Parameters.AddWithValue("@storyboardPath", artifacts.StoryboardPath);
            cmd.Parameters.AddWithValue("@hudStripPath", artifacts.HudStripPath);
            cmd.Parameters.AddWithValue("@minimapStripPath", artifacts.MinimapStripPath);
            cmd.Parameters.AddWithValue("@manifestPath", artifacts.ManifestPath);
            cmd.Parameters.AddWithValue("@noteText", payload.NoteText);
            cmd.Parameters.AddWithValue("@contextText", payload.ContextText);
            cmd.Parameters.AddWithValue("@createdAt", now);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        long momentId;
        using (var idCmd = connection.CreateCommand())
        {
            idCmd.Transaction = tx;
            idCmd.CommandText = "SELECT last_insert_rowid()";
            momentId = (long)(await idCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        var draft = await _sidecarClient.DraftMomentAsync(new CoachDraftRequest
        {
            NoteText = payload.NoteText,
            ReviewContext = payload.ContextText,
            ActiveObjectiveTitle = block.ObjectiveTitle,
            Champion = payload.Champion,
            Role = payload.Role,
            SourceType = payload.SourceType,
            GameTimeS = payload.GameTimeS,
            StoryboardPath = artifacts.StoryboardPath,
            MinimapStripPath = artifacts.MinimapStripPath,
        }, cancellationToken);

        using (var inferenceCmd = connection.CreateCommand())
        {
            inferenceCmd.Transaction = tx;
            inferenceCmd.CommandText = """
                INSERT INTO coach_inferences
                    (moment_id, player_id, model_version, inference_mode, moment_quality, primary_reason, objective_key, confidence, rationale, raw_payload, created_at, updated_at)
                VALUES
                    (@momentId, @playerId, @modelVersion, @inferenceMode, @momentQuality, @primaryReason, @objectiveKey, @confidence, @rationale, @rawPayload, @createdAt, @updatedAt)
                """;
            inferenceCmd.Parameters.AddWithValue("@momentId", momentId);
            inferenceCmd.Parameters.AddWithValue("@playerId", payload.PlayerId);
            inferenceCmd.Parameters.AddWithValue("@modelVersion", draft.ModelVersion);
            inferenceCmd.Parameters.AddWithValue("@inferenceMode", draft.InferenceMode);
            inferenceCmd.Parameters.AddWithValue("@momentQuality", draft.MomentQuality);
            inferenceCmd.Parameters.AddWithValue("@primaryReason", draft.PrimaryReason);
            inferenceCmd.Parameters.AddWithValue("@objectiveKey", draft.ObjectiveKey);
            inferenceCmd.Parameters.AddWithValue("@confidence", draft.Confidence);
            inferenceCmd.Parameters.AddWithValue("@rationale", draft.Rationale);
            inferenceCmd.Parameters.AddWithValue("@rawPayload", draft.RawPayload);
            inferenceCmd.Parameters.AddWithValue("@createdAt", now);
            inferenceCmd.Parameters.AddWithValue("@updatedAt", now);
            await inferenceCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private async Task<int> EnsureDraftsAsync(SqliteConnection connection, long playerId, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                m.id,
                m.note_text,
                m.context_text,
                m.champion,
                m.role,
                m.source_type,
                COALESCE(b.objective_title, ''),
                COALESCE(m.game_time_s, 0),
                COALESCE(m.storyboard_path, ''),
                COALESCE(m.minimap_strip_path, '')
            FROM coach_moments m
            LEFT JOIN coach_inferences i ON i.moment_id = m.id
            LEFT JOIN coach_objective_blocks b ON b.id = m.objective_block_id
            WHERE m.player_id = @playerId
              AND i.id IS NULL
            ORDER BY m.created_at DESC
            LIMIT 25
            """;
        cmd.Parameters.AddWithValue("@playerId", playerId);

        var pending = new List<(long Id, string NoteText, string ContextText, string Champion, string Role, string SourceType, string ObjectiveTitle, int GameTimeS, string StoryboardPath, string MinimapStripPath)>();
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                    reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    reader.IsDBNull(8) ? "" : reader.GetString(8),
                    reader.IsDBNull(9) ? "" : reader.GetString(9)));
            }
        }

        var created = 0;
        foreach (var item in pending)
        {
            var draft = await _sidecarClient.DraftMomentAsync(new CoachDraftRequest
            {
                NoteText = item.NoteText,
                ReviewContext = item.ContextText,
                Champion = item.Champion,
                Role = item.Role,
                SourceType = item.SourceType,
                ActiveObjectiveTitle = item.ObjectiveTitle,
                GameTimeS = item.GameTimeS,
                StoryboardPath = item.StoryboardPath,
                MinimapStripPath = item.MinimapStripPath,
            }, cancellationToken);

            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO coach_inferences
                    (moment_id, player_id, model_version, inference_mode, moment_quality, primary_reason, objective_key, confidence, rationale, raw_payload, created_at, updated_at)
                VALUES
                    (@momentId, @playerId, @modelVersion, @inferenceMode, @momentQuality, @primaryReason, @objectiveKey, @confidence, @rationale, @rawPayload, @createdAt, @updatedAt)
                """;
            insertCmd.Parameters.AddWithValue("@momentId", item.Id);
            insertCmd.Parameters.AddWithValue("@playerId", playerId);
            insertCmd.Parameters.AddWithValue("@modelVersion", draft.ModelVersion);
            insertCmd.Parameters.AddWithValue("@inferenceMode", draft.InferenceMode);
            insertCmd.Parameters.AddWithValue("@momentQuality", draft.MomentQuality);
            insertCmd.Parameters.AddWithValue("@primaryReason", draft.PrimaryReason);
            insertCmd.Parameters.AddWithValue("@objectiveKey", draft.ObjectiveKey);
            insertCmd.Parameters.AddWithValue("@confidence", draft.Confidence);
            insertCmd.Parameters.AddWithValue("@rationale", draft.Rationale);
            insertCmd.Parameters.AddWithValue("@rawPayload", draft.RawPayload);
            insertCmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            insertCmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            created++;
        }

        return created;
    }

    private async Task<int> RefreshArtifactsIfNeededAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                id,
                player_id,
                objective_block_id,
                game_id,
                bookmark_id,
                source_type,
                champion,
                role,
                game_time_s,
                clip_start_s,
                clip_end_s,
                clip_path,
                note_text,
                context_text,
                storyboard_path,
                hud_strip_path,
                minimap_strip_path,
                manifest_path
            FROM coach_moments
            WHERE COALESCE(clip_path, '') <> ''
            ORDER BY id ASC
            """;

        var pending = new List<(long Id, PendingMoment Payload, string StoryboardPath, string HudPath, string MinimapPath, string ManifestPath)>();
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var payload = new PendingMoment
                {
                    PlayerId = reader.GetInt64(1),
                    ObjectiveBlockId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    GameId = reader.GetInt64(3),
                    BookmarkId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    SourceType = reader.IsDBNull(5) ? "manual_clip" : reader.GetString(5),
                    Champion = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    Role = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    GameTimeS = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    ClipStartS = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    ClipEndS = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    ClipPath = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    NoteText = reader.IsDBNull(12) ? "" : reader.GetString(12),
                    ContextText = reader.IsDBNull(13) ? "" : reader.GetString(13),
                };

                pending.Add((
                    reader.GetInt64(0),
                    payload,
                    reader.IsDBNull(14) ? "" : reader.GetString(14),
                    reader.IsDBNull(15) ? "" : reader.GetString(15),
                    reader.IsDBNull(16) ? "" : reader.GetString(16),
                    reader.IsDBNull(17) ? "" : reader.GetString(17)));
            }
        }

        var refreshed = 0;
        foreach (var item in pending)
        {
            if (!NeedsArtifactRefresh(item.ManifestPath, item.StoryboardPath, item.MinimapPath))
            {
                continue;
            }

            var artifacts = await BuildArtifactsAsync(item.Payload, cancellationToken);
            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE coach_moments
                SET storyboard_path = @storyboardPath,
                    hud_strip_path = @hudPath,
                    minimap_strip_path = @minimapPath,
                    manifest_path = @manifestPath
                WHERE id = @id
                """;
            updateCmd.Parameters.AddWithValue("@storyboardPath", artifacts.StoryboardPath);
            updateCmd.Parameters.AddWithValue("@hudPath", artifacts.HudStripPath);
            updateCmd.Parameters.AddWithValue("@minimapPath", artifacts.MinimapStripPath);
            updateCmd.Parameters.AddWithValue("@manifestPath", artifacts.ManifestPath);
            updateCmd.Parameters.AddWithValue("@id", item.Id);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            refreshed++;
        }

        return refreshed;
    }

    private async Task UpdateDatasetVersionAsync(SqliteConnection connection, long playerId, CancellationToken cancellationToken)
    {
        var gold = await ExecuteCountAsync(connection, """
            SELECT COUNT(*)
            FROM coach_labels
            WHERE player_id = @playerId AND source = 'manual'
            """, playerId, cancellationToken);

        var silver = await ExecuteCountAsync(connection, """
            SELECT COUNT(*)
            FROM coach_moments m
            WHERE m.player_id = @playerId
              AND EXISTS (SELECT 1 FROM coach_inferences i WHERE i.moment_id = m.id)
              AND NOT EXISTS (SELECT 1 FROM coach_labels l WHERE l.moment_id = m.id)
              AND m.source_type = 'manual_clip'
            """, playerId, cancellationToken);

        var bronze = await ExecuteCountAsync(connection, """
            SELECT COUNT(*)
            FROM coach_moments m
            WHERE m.player_id = @playerId
              AND NOT EXISTS (SELECT 1 FROM coach_labels l WHERE l.moment_id = m.id)
              AND m.source_type = 'auto_sample'
            """, playerId, cancellationToken);

        var reviewedGames = await ExecuteCountAsync(connection, """
            SELECT COUNT(DISTINCT game_id)
            FROM coach_moments m
            WHERE m.player_id = @playerId
              AND (
                EXISTS (SELECT 1 FROM coach_labels l WHERE l.moment_id = m.id)
                OR m.source_type = 'manual_clip'
              )
            """, playerId, cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE coach_dataset_versions
            SET gold_count = @goldCount,
                silver_count = @silverCount,
                bronze_count = @bronzeCount,
                reviewed_games = @reviewedGames,
                updated_at = @updatedAt
            WHERE dataset_version = 'bootstrap-v1'
            """;
        cmd.Parameters.AddWithValue("@goldCount", gold);
        cmd.Parameters.AddWithValue("@silverCount", silver);
        cmd.Parameters.AddWithValue("@bronzeCount", bronze);
        cmd.Parameters.AddWithValue("@reviewedGames", reviewedGames);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(int Total, int Gold, int Silver, int Bronze, int Pending, int ReviewedGames)> GetDatasetCountsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var total = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM coach_moments", cancellationToken);
        var pending = await ExecuteScalarIntAsync(connection, """
            SELECT COUNT(*)
            FROM coach_moments m
            LEFT JOIN coach_labels l ON l.moment_id = m.id
            WHERE COALESCE(l.label_quality, '') = ''
            """, cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT gold_count, silver_count, bronze_count, reviewed_games
            FROM coach_dataset_versions
            WHERE dataset_version = 'bootstrap-v1'
            LIMIT 1
            """;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (
                total,
                reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                pending,
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
        }

        return (total, 0, 0, 0, pending, 0);
    }

    private async Task<int> ExecuteCountAsync(
        SqliteConnection connection,
        string sql,
        long playerId,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@playerId", playerId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    private async Task<int> ExecuteScalarIntAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    private async Task<CoachRecommendation?> GetLatestRecommendationAsync(
        SqliteConnection connection,
        long objectiveBlockId,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, objective_block_id, player_id, recommendation_type, state, objective_key, title, summary, confidence, evidence_game_count, raw_payload, created_at, updated_at
            FROM coach_recommendations
            WHERE objective_block_id = @objectiveBlockId
            ORDER BY created_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@objectiveBlockId", objectiveBlockId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CoachRecommendation
        {
            Id = reader.GetInt64(0),
            ObjectiveBlockId = reader.GetInt64(1),
            PlayerId = reader.GetInt64(2),
            RecommendationType = reader.IsDBNull(3) ? "keep" : reader.GetString(3),
            State = reader.IsDBNull(4) ? "draft" : reader.GetString(4),
            ObjectiveKey = reader.IsDBNull(5) ? "" : reader.GetString(5),
            Title = reader.IsDBNull(6) ? "" : reader.GetString(6),
            Summary = reader.IsDBNull(7) ? "" : reader.GetString(7),
            Confidence = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
            EvidenceGameCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            RawPayload = reader.IsDBNull(10) ? "{}" : reader.GetString(10),
            CreatedAt = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
            UpdatedAt = reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
        };
    }

    private static string BuildContextText(
        string reviewNotes,
        string mistakes,
        string focusNext,
        string wentWell,
        string spottedProblems)
    {
        var parts = new[]
        {
            reviewNotes,
            mistakes,
            focusNext,
            wentWell,
            spottedProblems
        };

        return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim()));
    }

    private static IReadOnlyList<int> GetAutoSampleSeconds(int gameDurationSeconds)
    {
        var results = new List<int>();
        var max = Math.Min(gameDurationSeconds, 600);
        for (var second = 150; second < max; second += 120)
        {
            results.Add(second);
        }

        return results;
    }

    private async Task<CoachArtifacts> BuildArtifactsAsync(PendingMoment payload, CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            AppDataPaths.CoachAnalysisDirectory,
            "moments",
            payload.SourceType == "manual_clip"
                ? $"bookmark-{payload.BookmarkId ?? 0}"
                : $"auto-{payload.GameId}-{payload.GameTimeS}");

        Directory.CreateDirectory(root);

        var storyboardPath = Path.Combine(root, "storyboard.jpg");
        var minimapPath = Path.Combine(root, "minimap.jpg");
        var manifestPath = Path.Combine(root, "moment.json");

        var ffmpeg = await _clipService.FindFfmpegAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(payload.ClipPath))
        {
            await TryRunFfmpegAsync(ffmpeg, payload.ClipPath, "fps=1.5,scale=426:-1,tile=3x2", storyboardPath, cancellationToken);
            await TryRunFfmpegAsync(
                ffmpeg,
                payload.ClipPath,
                "fps=1.5,crop=trunc(iw*0.19):trunc(ih*0.28):trunc(iw*0.81):trunc(ih*0.70),scale=220:-1:flags=lanczos,tile=6x1",
                minimapPath,
                cancellationToken);
        }

        var manifest = new
        {
            artifactVersion = CoachArtifactVersion,
            payload.SourceType,
            payload.GameId,
            payload.BookmarkId,
            payload.Champion,
            payload.Role,
            payload.GameTimeS,
            payload.ClipStartS,
            payload.ClipEndS,
            payload.ClipPath,
            payload.NoteText,
            payload.ContextText,
            storyboardPath = File.Exists(storyboardPath) ? storyboardPath : "",
            minimapPath = File.Exists(minimapPath) ? minimapPath : ""
        };

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);

        return new CoachArtifacts
        {
            StoryboardPath = File.Exists(storyboardPath) ? storyboardPath : "",
            HudStripPath = "",
            MinimapStripPath = File.Exists(minimapPath) ? minimapPath : "",
            ManifestPath = manifestPath,
        };
    }

    private static bool NeedsArtifactRefresh(
        string manifestPath,
        string storyboardPath,
        string minimapPath)
    {
        if (!File.Exists(storyboardPath) || !File.Exists(minimapPath))
        {
            return true;
        }

        if (!File.Exists(manifestPath))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("artifactVersion", out var versionElement))
            {
                return true;
            }

            return versionElement.ValueKind != JsonValueKind.Number
                || versionElement.GetInt32() < CoachArtifactVersion;
        }
        catch
        {
            return true;
        }
    }

    private async Task TryRunFfmpegAsync(
        string ffmpeg,
        string inputPath,
        string filter,
        string outputPath,
        CancellationToken cancellationToken)
    {
        const int artifactTimeoutS = 12;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(filter);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add(outputPath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // Best effort only.
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(artifactTimeoutS));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                _logger.LogDebug("Coach artifact extraction timed out after {Timeout}s for {OutputPath}", artifactTimeoutS, outputPath);
                TryDeleteFile(outputPath);
                return;
            }

            await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("ffmpeg asset extraction failed for {OutputPath}: {Error}", outputPath, stderr);
                TryDeleteFile(outputPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create coach artifact {Path}", outputPath);
            TryDeleteFile(outputPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static CoachObjectiveBlock ReadObjectiveBlock(SqliteDataReader reader)
    {
        return new CoachObjectiveBlock
        {
            Id = reader.GetInt64(0),
            PlayerId = reader.GetInt64(1),
            ObjectiveId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            ObjectiveTitle = reader.IsDBNull(3) ? "" : reader.GetString(3),
            ObjectiveKey = reader.IsDBNull(4) ? "" : reader.GetString(4),
            Status = reader.IsDBNull(5) ? "active" : reader.GetString(5),
            Mode = reader.IsDBNull(6) ? "assist" : reader.GetString(6),
            StartedAt = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
            UpdatedAt = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
            CompletedAt = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            Notes = reader.IsDBNull(10) ? "" : reader.GetString(10)
        };
    }

    private static CoachMomentCard ReadMomentCard(SqliteDataReader reader)
    {
        return new CoachMomentCard
        {
            Id = reader.GetInt64(0),
            GameId = reader.GetInt64(1),
            BookmarkId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            SourceType = reader.IsDBNull(3) ? "manual_clip" : reader.GetString(3),
            Champion = reader.IsDBNull(4) ? "" : reader.GetString(4),
            Role = reader.IsDBNull(5) ? "" : reader.GetString(5),
            GameTimeS = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            ClipPath = reader.IsDBNull(7) ? "" : reader.GetString(7),
            StoryboardPath = reader.IsDBNull(8) ? "" : reader.GetString(8),
            HudStripPath = reader.IsDBNull(9) ? "" : reader.GetString(9),
            MinimapStripPath = reader.IsDBNull(10) ? "" : reader.GetString(10),
            ManifestPath = reader.IsDBNull(11) ? "" : reader.GetString(11),
            NoteText = reader.IsDBNull(12) ? "" : reader.GetString(12),
            ContextText = reader.IsDBNull(13) ? "" : reader.GetString(13),
            DraftQuality = reader.IsDBNull(14) ? "neutral" : reader.GetString(14),
            DraftPrimaryReason = reader.IsDBNull(15) ? "" : reader.GetString(15),
            DraftObjectiveKey = reader.IsDBNull(16) ? "" : reader.GetString(16),
            DraftAttachedObjectiveId = reader.IsDBNull(17) ? null : reader.GetInt64(17),
            DraftAttachedObjectiveTitle = reader.IsDBNull(18) ? "" : reader.GetString(18),
            DraftConfidence = reader.IsDBNull(19) ? 0 : reader.GetDouble(19),
            DraftRationale = reader.IsDBNull(20) ? "" : reader.GetString(20),
            LabelQuality = reader.IsDBNull(21) ? "" : reader.GetString(21),
            LabelPrimaryReason = reader.IsDBNull(22) ? "" : reader.GetString(22),
            LabelObjectiveKey = reader.IsDBNull(23) ? "" : reader.GetString(23),
            LabelAttachedObjectiveId = reader.IsDBNull(24) ? null : reader.GetInt64(24),
            LabelAttachedObjectiveTitle = reader.IsDBNull(25) ? "" : reader.GetString(25),
            LabelExplanation = reader.IsDBNull(26) ? "" : reader.GetString(26),
            LabelConfidence = reader.IsDBNull(27) ? 0 : reader.GetDouble(27),
            BlockObjectiveId = reader.IsDBNull(28) ? null : reader.GetInt64(28),
            BlockObjectiveTitle = reader.IsDBNull(29) ? "" : reader.GetString(29),
            CreatedAt = reader.IsDBNull(30) ? 0 : reader.GetInt64(30),
            ReviewedAt = reader.IsDBNull(31) ? null : reader.GetInt64(31),
        };
    }

    private static async Task<string> LookupObjectiveTitleAsync(
        SqliteConnection connection,
        long? objectiveId,
        CancellationToken cancellationToken)
    {
        if (!objectiveId.HasValue)
        {
            return "";
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(title, '') FROM objectives WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", objectiveId.Value);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result?.ToString() ?? "";
    }

    private static string BuildWatchItemTitle(CoachRecommendation? recommendation)
    {
        return recommendation?.RecommendationType == "watch"
            ? "Watch item"
            : "Clip-backed evidence";
    }

    private static string BuildWatchItemSummary(CoachRecommendation? recommendation)
    {
        if (recommendation is null)
        {
            return "Keep saving lane clips with notes so the coach can learn from exact moments.";
        }

        if (recommendation.RecommendationType != "watch" || string.IsNullOrWhiteSpace(recommendation.ObjectiveKey))
        {
            return recommendation.Summary;
        }

        var title = CoachObjectiveCatalog.Find(recommendation.ObjectiveKey)?.Title ?? HumanizeKey(recommendation.ObjectiveKey);
        return $"Emerging theme: {title}. {recommendation.Summary}";
    }

    private async Task<MomentLabelContext> LoadMomentContextForLabelAsync(
        SqliteConnection connection,
        long momentId,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(m.note_text, ''),
                COALESCE(m.context_text, ''),
                COALESCE(m.champion, ''),
                COALESCE(m.role, ''),
                COALESCE(m.source_type, 'manual_clip'),
                COALESCE(m.game_time_s, 0),
                COALESCE(m.storyboard_path, ''),
                COALESCE(m.minimap_strip_path, ''),
                COALESCE(b.objective_title, '')
            FROM coach_moments m
            LEFT JOIN coach_objective_blocks b ON b.id = m.objective_block_id
            WHERE m.id = @momentId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@momentId", momentId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new MomentLabelContext();
        }

        return new MomentLabelContext
        {
            NoteText = reader.IsDBNull(0) ? "" : reader.GetString(0),
            ContextText = reader.IsDBNull(1) ? "" : reader.GetString(1),
            Champion = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Role = reader.IsDBNull(3) ? "" : reader.GetString(3),
            SourceType = reader.IsDBNull(4) ? "manual_clip" : reader.GetString(4),
            GameTimeS = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            StoryboardPath = reader.IsDBNull(6) ? "" : reader.GetString(6),
            MinimapStripPath = reader.IsDBNull(7) ? "" : reader.GetString(7),
            ActiveObjectiveTitle = reader.IsDBNull(8) ? "" : reader.GetString(8),
        };
    }

    private async Task<string> InferPrimaryReasonAsync(
        MomentLabelContext context,
        string attachedObjectiveTitle,
        CoachManualLabelInput input,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(input.PrimaryReason))
        {
            return input.PrimaryReason.Trim();
        }

        var draft = await _sidecarClient.DraftMomentAsync(new CoachDraftRequest
        {
            NoteText = string.Join(Environment.NewLine, new[]
            {
                context.NoteText,
                input.Explanation
            }.Where(value => !string.IsNullOrWhiteSpace(value))),
            ReviewContext = string.Join(Environment.NewLine, new[]
            {
                context.ContextText,
                attachedObjectiveTitle
            }.Where(value => !string.IsNullOrWhiteSpace(value))),
            ActiveObjectiveTitle = !string.IsNullOrWhiteSpace(attachedObjectiveTitle)
                ? attachedObjectiveTitle
                : context.ActiveObjectiveTitle,
            Champion = context.Champion,
            Role = context.Role,
            SourceType = context.SourceType,
            GameTimeS = context.GameTimeS,
            StoryboardPath = context.StoryboardPath,
            MinimapStripPath = context.MinimapStripPath,
        }, cancellationToken);

        if (!string.IsNullOrWhiteSpace(draft.PrimaryReason))
        {
            return draft.PrimaryReason.Trim();
        }

        var fallback = CoachDraftHeuristics.InferObjectiveKey(
            context.NoteText,
            context.ContextText,
            input.Explanation,
            attachedObjectiveTitle,
            context.ActiveObjectiveTitle);

        return string.IsNullOrWhiteSpace(fallback)
            ? (context.SourceType == "manual_clip" ? "manual_clip_review" : "lane_checkpoint")
            : fallback;
    }

    private static string HumanizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Needs review";
        }

        return string.Join(' ',
            value.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private sealed class PendingMoment
    {
        public long PlayerId { get; set; }
        public long GameId { get; set; }
        public long? BookmarkId { get; set; }
        public long? ObjectiveBlockId { get; set; }
        public string SourceType { get; set; } = "manual_clip";
        public string Champion { get; set; } = "";
        public string Role { get; set; } = "";
        public int GameTimeS { get; set; }
        public int? ClipStartS { get; set; }
        public int? ClipEndS { get; set; }
        public string ClipPath { get; set; } = "";
        public string NoteText { get; set; } = "";
        public string ContextText { get; set; } = "";
    }

    private sealed class CoachArtifacts
    {
        public string StoryboardPath { get; set; } = "";
        public string HudStripPath { get; set; } = "";
        public string MinimapStripPath { get; set; } = "";
        public string ManifestPath { get; set; } = "";
    }

    private sealed class MomentLabelContext
    {
        public string NoteText { get; set; } = "";
        public string ContextText { get; set; } = "";
        public string Champion { get; set; } = "";
        public string Role { get; set; } = "";
        public string SourceType { get; set; } = "manual_clip";
        public int GameTimeS { get; set; }
        public string StoryboardPath { get; set; } = "";
        public string MinimapStripPath { get; set; } = "";
        public string ActiveObjectiveTitle { get; set; } = "";
    }
}

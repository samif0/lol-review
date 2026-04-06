using System.Text.Json;
using LoLReview.Core.Data;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoLReview.Core.Tests;

[CollectionDefinition("Coach Lab feature", DisableParallelization = true)]
public sealed class CoachLabFeatureCollection
{
}

[Collection("Coach Lab feature")]
public sealed class CoachLabServiceTests
{
    [Fact]
    public async Task GetDashboardAsync_AutoLabelsExistingManualClipsFromReviewNotes()
    {
        using var featureScope = new CoachLabFeatureScope();
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var service = CreateService(scope.ConnectionFactory);
        await service.GetDashboardAsync();

        const long gameId = 5511223399;
        const string reviewNotes = "Hold the wave, respect jungle support threat, and only step up when the engage angle is gone.";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using (var connection = scope.OpenConnection())
        {
            var playerId = await ExecuteScalarAsync<long>(connection, "SELECT id FROM coach_players LIMIT 1");
            var blockId = await ExecuteScalarAsync<long>(connection, "SELECT id FROM coach_objective_blocks WHERE player_id = @playerId LIMIT 1", ("@playerId", playerId));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO games (
                    game_id, timestamp, date_played, game_duration, game_mode,
                    game_type, queue_type, summoner_name, champion_name, champion_id,
                    team_id, position, role, win, review_notes
                )
                VALUES (
                    @gameId, @timestamp, '2026-04-06 09:30', 1800, 'Ranked Solo',
                    'MATCHED_GAME', '420', 'Tester', 'Kai''Sa', 145,
                    100, 'BOTTOM', 'carry', 1, @reviewNotes
                )
                """,
                ("@gameId", gameId),
                ("@timestamp", now),
                ("@reviewNotes", reviewNotes));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_moments (
                    id, player_id, game_id, objective_block_id, source_type, patch_version, champion, role, game_time_s,
                    clip_path, storyboard_path, hud_strip_path, minimap_strip_path, manifest_path, note_text, context_text,
                    dataset_version, model_version, created_at
                )
                VALUES (
                    9000, @playerId, @gameId, @blockId, 'manual_clip', 'unknown', 'Kai''Sa', 'carry', 186,
                    '', '', '', '', '', 'Walked up into engage range', @contextText,
                    'bootstrap-v1', 'assist-heuristic-v1', @createdAt
                )
                """,
                ("@playerId", playerId),
                ("@gameId", gameId),
                ("@blockId", blockId),
                ("@contextText", reviewNotes),
                ("@createdAt", now));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_inferences (
                    moment_id, player_id, model_version, inference_mode, moment_quality, primary_reason, objective_key,
                    confidence, rationale, raw_payload, created_at, updated_at
                )
                VALUES (
                    9000, @playerId, 'assist-heuristic-v1', 'assist', 'bad', 'respect engage support',
                    'respect_jungle_support_threat', 0.55, 'Draft rationale', '{}', @createdAt, @updatedAt
                )
                """,
                ("@playerId", playerId),
                ("@createdAt", now),
                ("@updatedAt", now));
        }

        var dashboard = await service.GetDashboardAsync();
        var moment = Assert.Single(await service.GetMomentQueueAsync());

        Assert.Equal(1, dashboard.GoldMoments);
        Assert.Equal(0, dashboard.PendingMoments);
        Assert.True(moment.HasManualLabel);
        Assert.Equal(reviewNotes, moment.LabelPrimaryReason);
    }

    [Fact]
    public async Task SyncMomentsAsync_UsesReviewNotesAsFinalTagsForExistingManualClips()
    {
        using var featureScope = new CoachLabFeatureScope();
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var service = CreateService(scope.ConnectionFactory);
        await service.GetDashboardAsync();

        const long gameId = 5511223344;
        const string reviewNotes = "Respect jungle and support threat before walking up for the cannon wave.";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using (var connection = scope.OpenConnection())
        {
            var playerId = await ExecuteScalarAsync<long>(connection, "SELECT id FROM coach_players LIMIT 1");
            var blockId = await ExecuteScalarAsync<long>(connection, "SELECT id FROM coach_objective_blocks WHERE player_id = @playerId LIMIT 1", ("@playerId", playerId));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO games (
                    game_id, timestamp, date_played, game_duration, game_mode,
                    game_type, queue_type, summoner_name, champion_name, champion_id,
                    team_id, position, role, win, review_notes
                )
                VALUES (
                    @gameId, @timestamp, '2026-04-06 10:00', 1800, 'Ranked Solo',
                    'MATCHED_GAME', '420', 'Tester', 'Kai''Sa', 145,
                    100, 'BOTTOM', 'carry', 1, @reviewNotes
                )
                """,
                ("@gameId", gameId),
                ("@timestamp", now),
                ("@reviewNotes", reviewNotes));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_moments (
                    id, player_id, game_id, objective_block_id, source_type, patch_version, champion, role, game_time_s,
                    clip_path, storyboard_path, hud_strip_path, minimap_strip_path, manifest_path, note_text, context_text,
                    dataset_version, model_version, created_at
                )
                VALUES (
                    9001, @playerId, @gameId, @blockId, 'manual_clip', 'unknown', 'Kai''Sa', 'carry', 244,
                    '', '', '', '', '', 'Stepped up for cannon', @contextText,
                    'bootstrap-v1', 'assist-heuristic-v1', @createdAt
                )
                """,
                ("@playerId", playerId),
                ("@gameId", gameId),
                ("@blockId", blockId),
                ("@contextText", $"{reviewNotes} | Need to track Nautilus roam timers."),
                ("@createdAt", now));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_inferences (
                    moment_id, player_id, model_version, inference_mode, moment_quality, primary_reason, objective_key,
                    confidence, rationale, raw_payload, created_at, updated_at
                )
                VALUES (
                    9001, @playerId, 'assist-heuristic-v1', 'assist', 'bad', 'respect engage support',
                    'respect_jungle_support_threat', 0.55, 'Draft rationale', '{}', @createdAt, @updatedAt
                )
                """,
                ("@playerId", playerId),
                ("@createdAt", now),
                ("@updatedAt", now));
        }

        var result = await service.SyncMomentsAsync(includeAutoSamples: false);

        Assert.Equal(0, result.ManualClipsImported);
        Assert.Equal(0, result.DraftsCreated);
        Assert.Equal(1, result.ReviewNoteLabelsApplied);

        var moment = Assert.Single(await service.GetMomentQueueAsync());
        Assert.True(moment.HasManualLabel);
        Assert.Equal("bad", moment.LabelQuality);
        Assert.Equal(reviewNotes, moment.LabelPrimaryReason);
        Assert.Equal("respect_jungle_support_threat", moment.LabelObjectiveKey);
        Assert.True(moment.LabelConfidence >= 0.85);
    }

    [Fact]
    public async Task SaveManualLabelAsync_PreservesExistingPrimaryReasonAndObjectiveKeyWhenUiLeavesThemBlank()
    {
        using var featureScope = new CoachLabFeatureScope();
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var service = CreateService(scope.ConnectionFactory);
        await service.GetDashboardAsync();

        const long gameId = 5511223355;
        const string reviewNotes = "Crash the wave before resetting so the base timing is clean.";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using (var connection = scope.OpenConnection())
        {
            var playerId = await ExecuteScalarAsync<long>(connection, "SELECT id FROM coach_players LIMIT 1");
            var blockId = await ExecuteScalarAsync<long>(connection, "SELECT id FROM coach_objective_blocks WHERE player_id = @playerId LIMIT 1", ("@playerId", playerId));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO games (
                    game_id, timestamp, date_played, game_duration, game_mode,
                    game_type, queue_type, summoner_name, champion_name, champion_id,
                    team_id, position, role, win, review_notes
                )
                VALUES (
                    @gameId, @timestamp, '2026-04-06 11:00', 1800, 'Ranked Solo',
                    'MATCHED_GAME', '420', 'Tester', 'Kai''Sa', 145,
                    100, 'BOTTOM', 'carry', 1, @reviewNotes
                )
                """,
                ("@gameId", gameId),
                ("@timestamp", now),
                ("@reviewNotes", reviewNotes));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_moments (
                    id, player_id, game_id, objective_block_id, source_type, patch_version, champion, role, game_time_s,
                    clip_path, storyboard_path, hud_strip_path, minimap_strip_path, manifest_path, note_text, context_text,
                    dataset_version, model_version, created_at
                )
                VALUES (
                    9002, @playerId, @gameId, @blockId, 'manual_clip', 'unknown', 'Kai''Sa', 'carry', 310,
                    '', '', '', '', '', 'Stayed too long after crash', @contextText,
                    'bootstrap-v1', 'assist-heuristic-v1', @createdAt
                )
                """,
                ("@playerId", playerId),
                ("@gameId", gameId),
                ("@blockId", blockId),
                ("@contextText", reviewNotes),
                ("@createdAt", now));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO coach_labels (
                    moment_id, player_id, label_quality, primary_reason, objective_key, explanation,
                    confidence, source, created_at, updated_at
                )
                VALUES (
                    9002, @playerId, 'neutral', @primaryReason, 'recall_on_crash_and_tempo', '',
                    0.9, 'manual', @createdAt, @updatedAt
                )
                """,
                ("@playerId", playerId),
                ("@primaryReason", reviewNotes),
                ("@createdAt", now),
                ("@updatedAt", now));
        }

        await service.SaveManualLabelAsync(9002, new CoachManualLabelInput
        {
            LabelQuality = "good",
            PrimaryReason = "",
            ObjectiveKey = "",
            Confidence = 0.65,
        });

        await using var verificationConnection = scope.OpenConnection();
        var updatedPrimaryReason = await ExecuteScalarAsync<string>(verificationConnection, """
            SELECT primary_reason
            FROM coach_labels
            WHERE moment_id = 9002
            """);
        var updatedObjectiveKey = await ExecuteScalarAsync<string>(verificationConnection, """
            SELECT objective_key
            FROM coach_labels
            WHERE moment_id = 9002
            """);
        var updatedQuality = await ExecuteScalarAsync<string>(verificationConnection, """
            SELECT label_quality
            FROM coach_labels
            WHERE moment_id = 9002
            """);

        Assert.Equal(reviewNotes, updatedPrimaryReason);
        Assert.Equal("recall_on_crash_and_tempo", updatedObjectiveKey);
        Assert.Equal("good", updatedQuality);
    }

    private static CoachLabService CreateService(IDbConnectionFactory connectionFactory)
    {
        return new CoachLabService(
            connectionFactory,
            new StubClipService(),
            new StubCoachSidecarClient(),
            new StubCoachRecommendationService(),
            new StubCoachTrainingService(),
            NullLogger<CoachLabService>.Instance);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    private sealed class StubClipService : IClipService
    {
        public Task<string?> FindFfmpegAsync() => Task.FromResult<string?>(null);

        public Task<string?> ExtractClipAsync(string vodPath, int startS, int endS, string champion, string? outputFolder = null) =>
            Task.FromResult<string?>(null);

        public Task EnforceFolderSizeLimitAsync(string folder, long maxSizeBytes) => Task.CompletedTask;
    }

    private sealed class StubCoachSidecarClient : ICoachSidecarClient
    {
        public Task<CoachDraftResult> DraftMomentAsync(CoachDraftRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CoachDraftResult
            {
                ModelVersion = "assist-heuristic-v1",
                InferenceMode = "assist",
                MomentQuality = "bad",
                PrimaryReason = "respect engage support",
                ObjectiveKey = "respect_jungle_support_threat",
                Confidence = 0.55,
                Rationale = "Stub draft",
                RawPayload = "{}",
            });
        }
    }

    private sealed class StubCoachRecommendationService : ICoachRecommendationService
    {
        public Task<CoachRecommendation> BuildAssistRecommendationAsync(long playerId, CoachObjectiveBlock block, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CoachRecommendation
            {
                ObjectiveBlockId = block.Id,
                PlayerId = playerId,
                RecommendationType = "keep",
                Title = "Assist mode active",
                Summary = "Stub recommendation."
            });
        }

        public Task<CoachProblemsReport> BuildProblemsReportAsync(long playerId, CoachObjectiveBlock block, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CoachProblemsReport());
        }

        public Task<CoachObjectiveSuggestion> GenerateObjectiveSuggestionAsync(long playerId, CoachObjectiveBlock block, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CoachObjectiveSuggestion());
        }
    }

    private sealed class StubCoachTrainingService : ICoachTrainingService
    {
        public Task<CoachTrainingStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CoachTrainingStatus());
        }

        public Task<CoachTrainResult> TrainPrematureModelAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CoachTrainResult
            {
                Success = true,
                Summary = "Stub training."
            });
        }
    }

    private sealed class CoachLabFeatureScope : IDisposable
    {
        private readonly string? _previousEnvValue;
        private readonly string _accessFilePath;
        private readonly string? _previousAccessFileContents;
        private readonly bool _hadAccessFile;

        public CoachLabFeatureScope()
        {
            _previousEnvValue = Environment.GetEnvironmentVariable(CoachLabFeature.EnableEnvVar);
            Environment.SetEnvironmentVariable(CoachLabFeature.EnableEnvVar, "1");

            Directory.CreateDirectory(AppDataPaths.UserDataRoot);
            _accessFilePath = CoachLabFeature.AccessFilePath;
            _hadAccessFile = File.Exists(_accessFilePath);
            _previousAccessFileContents = _hadAccessFile
                ? File.ReadAllText(_accessFilePath)
                : null;

            var accessConfig = new CoachLabAccessConfig
            {
                AllowedWindowsUsers = [Environment.UserName]
            };

            File.WriteAllText(_accessFilePath, JsonSerializer.Serialize(accessConfig));
            CoachLabFeature.UpdateRuntimeIdentity(null, null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(CoachLabFeature.EnableEnvVar, _previousEnvValue);

            if (_hadAccessFile && _previousAccessFileContents is not null)
            {
                File.WriteAllText(_accessFilePath, _previousAccessFileContents);
            }
            else if (File.Exists(_accessFilePath))
            {
                File.Delete(_accessFilePath);
            }
        }
    }
}

#nullable enable

using System.Diagnostics;
using System.Text.Json;
using LoLReview.Core.Data;
using LoLReview.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

public sealed class CoachTrainingService : ICoachTrainingService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICoachSidecarClient _sidecarClient;
    private readonly ILogger<CoachTrainingService> _logger;
    private readonly object _stateLock = new();
    private Task? _activeTrainingTask;
    private TrainingRuntimeState _runtimeState = new();

    public CoachTrainingService(
        IDbConnectionFactory connectionFactory,
        ICoachSidecarClient sidecarClient,
        ILogger<CoachTrainingService> logger)
    {
        _connectionFactory = connectionFactory;
        _sidecarClient = sidecarClient;
        _logger = logger;
    }

    public async Task<CoachTrainingStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var gold = await ExecuteScalarIntAsync(connection, """
            SELECT COALESCE(SUM(gold_count), 0)
            FROM coach_dataset_versions
            WHERE status = 'active'
            """, cancellationToken);

        var silver = await ExecuteScalarIntAsync(connection, """
            SELECT COALESCE(SUM(silver_count), 0)
            FROM coach_dataset_versions
            WHERE status = 'active'
            """, cancellationToken);

        var reviewedGames = await ExecuteScalarIntAsync(connection, """
            SELECT COALESCE(SUM(reviewed_games), 0)
            FROM coach_dataset_versions
            WHERE status = 'active'
            """, cancellationToken);

        var activeAdapter = await GetLatestActiveModelAsync(connection, "gemma_adapter", cancellationToken);
        var activeBase = await GetLatestActiveModelAsync(connection, "gemma_base", cancellationToken);
        var runtimeState = GetRuntimeStateSnapshot();
        var labeledMoments = gold + silver;

        return new CoachTrainingStatus
        {
            Mode = activeAdapter is not null
                ? "gemma_adapter"
                : activeBase is not null
                    ? "gemma_base"
                    : "gemma_setup_required",
            ActiveModelVersion = activeAdapter?.ModelVersion ?? activeBase?.ModelVersion ?? "",
            ActiveAdapterVersion = activeAdapter?.ModelVersion ?? "",
            ActiveBaseModelVersion = activeBase?.ModelVersion ?? "",
            IsTrainingInProgress = runtimeState.IsTrainingInProgress,
            ActiveTrainingKind = runtimeState.ActiveTrainingKind,
            ActiveTrainingStatusText = runtimeState.ActiveTrainingStatusText,
            ActiveTrainingStartedAt = runtimeState.ActiveTrainingStartedAt,
            LastTrainingSummary = runtimeState.LastTrainingSummary,
            LastTrainingCompletedAt = runtimeState.LastTrainingCompletedAt,
            LastTrainingSucceeded = runtimeState.LastTrainingSucceeded,
            GoldMoments = gold,
            LabeledMoments = labeledMoments,
            ReviewedGames = reviewedGames,
            CanPrepareGemmaDataset = labeledMoments >= 2,
            CanFineTuneGemma = gold >= 20 && reviewedGames >= 5,
            HasGemmaBaseModel = activeBase is not null,
            HasGemmaAdapter = activeAdapter is not null,
        };
    }

    public async Task<CoachTrainResult> TrainGemmaModelAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);

        lock (_stateLock)
        {
            if (_activeTrainingTask is not null && !_activeTrainingTask.IsCompleted)
            {
                return new CoachTrainResult
                {
                    Success = true,
                    StartedInBackground = true,
                    AlreadyRunning = true,
                    Summary = string.IsNullOrWhiteSpace(_runtimeState.ActiveTrainingStatusText)
                        ? "Gemma training is already running in the background."
                        : _runtimeState.ActiveTrainingStatusText,
                };
            }

            var startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _runtimeState = new TrainingRuntimeState
            {
                IsTrainingInProgress = true,
                ActiveTrainingKind = "gemma",
                ActiveTrainingStatusText = "Registering Gemma 4 E4B and preparing Coach Lab training artifacts...",
                ActiveTrainingStartedAt = startedAt,
                LastTrainingSummary = "",
                LastTrainingCompletedAt = null,
                LastTrainingSucceeded = false,
            };

            _activeTrainingTask = Task.Run(() => RunGemmaTrainingWorkflowAsync(status.CanPrepareGemmaDataset));
        }

        return new CoachTrainResult
        {
            Success = true,
            StartedInBackground = true,
            Summary = "Gemma training started in the background. Coach Lab will register the base model, prepare or train the adapter, then re-score saved moments.",
        };
    }

    private async Task RunGemmaTrainingWorkflowAsync(bool canPrepareDataset)
    {
        try
        {
            UpdateRuntimeState("Registering Gemma 4 E4B as the active Coach Lab base model...");
            var result = await TrainGemmaCoreAsync(canPrepareDataset, CancellationToken.None);

            UpdateRuntimeState("Re-scoring saved coach moments with the active Gemma model...");
            var rescored = await RedraftAllMomentsAsync(CancellationToken.None);
            result.RescoredMoments = rescored;
            result.Summary = $"{result.Summary} Re-scored {rescored} clip(s) with the active Gemma model.";

            CompleteRuntimeState(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background Gemma coach training failed");
            CompleteRuntimeState(ex);
        }
    }

    private async Task<CoachTrainResult> TrainGemmaCoreAsync(
        bool canPrepareDataset,
        CancellationToken cancellationToken)
    {
        var registerScript = CoachPythonRuntime.ResolveCoachLabScriptPath("register_gemma_e4b.py");
        var exportScript = CoachPythonRuntime.ResolveCoachLabScriptPath("export_dataset.py");
        var trainerScript = CoachPythonRuntime.ResolveCoachLabScriptPath("train_gemma_e4b.py");
        var exportDirectory = Path.Combine(AppDataPaths.CoachAnalysisDirectory, "exports", "bootstrap-v1");
        var modelsDirectory = Path.Combine(AppDataPaths.CoachAnalysisDirectory, "models", "gemma");
        Directory.CreateDirectory(exportDirectory);
        Directory.CreateDirectory(modelsDirectory);

        var registerOutput = await RunPythonScriptAsync(registerScript, "--activate", cancellationToken);
        var registerPayload = JsonSerializer.Deserialize<GemmaRegisterPayload>(registerOutput, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Gemma register script did not return valid JSON.");

        GemmaTrainPayload? trainPayload = null;
        if (canPrepareDataset)
        {
            UpdateRuntimeState("Exporting saved clips for Gemma fine-tuning...");
            await RunPythonScriptAsync(
                exportScript,
                $"--output \"{exportDirectory}\"",
                cancellationToken);

            UpdateRuntimeState("Preparing or training the Gemma adapter...");
            var trainerOutput = await RunPythonScriptAsync(
                trainerScript,
                $"--input \"{Path.Combine(exportDirectory, "moments.jsonl")}\" --output \"{modelsDirectory}\" --register",
                cancellationToken);

            trainPayload = JsonSerializer.Deserialize<GemmaTrainPayload>(trainerOutput, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Gemma trainer did not return valid JSON.");
        }

        var summary = canPrepareDataset
            ? BuildGemmaTrainingSummary(registerPayload, trainPayload)
            : $"Registered Gemma 4 E4B base model ({registerPayload.ModelVersion}). Not enough labeled clips yet to prepare a fine-tuning dataset.";

        return new CoachTrainResult
        {
            Success = true,
            ModelVersion = trainPayload?.RegisteredModelVersion
                ?? trainPayload?.ModelVersion
                ?? registerPayload.ModelVersion,
            Summary = summary,
            TrainingExamples = trainPayload?.TrainCount ?? 0,
            GoldExamples = trainPayload?.GoldExamples ?? 0,
            SilverExamples = trainPayload?.SilverExamples ?? 0,
            ModelDirectory = trainPayload?.ModelDirectory ?? "",
        };
    }

    private static string BuildGemmaTrainingSummary(
        GemmaRegisterPayload registerPayload,
        GemmaTrainPayload? trainPayload)
    {
        if (trainPayload is null)
        {
            return $"Registered Gemma 4 E4B base model ({registerPayload.ModelVersion}).";
        }

        if (trainPayload.Trained)
        {
            return
                $"Registered Gemma 4 E4B base model ({registerPayload.ModelVersion}) and trained a Gemma adapter from {trainPayload.TrainCount} clip(s) ({trainPayload.GoldExamples} gold, {trainPayload.SilverExamples} silver).";
        }

        return
            $"Registered Gemma 4 E4B base model ({registerPayload.ModelVersion}) and prepared Gemma adapter artifacts from {trainPayload.TrainCount} clip(s). Training was skipped: {trainPayload.Reason}.";
    }

    private async Task<CoachModelVersion?> GetLatestActiveModelAsync(
        SqliteConnection connection,
        string modelKind,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, model_version, model_kind, display_name, provider, is_active, metadata_json, created_at
            FROM coach_models
            WHERE model_kind = @modelKind AND is_active = 1
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@modelKind", modelKind);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CoachModelVersion
        {
            Id = reader.GetInt64(0),
            ModelVersion = reader.IsDBNull(1) ? "" : reader.GetString(1),
            ModelKind = reader.IsDBNull(2) ? "" : reader.GetString(2),
            DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Provider = reader.IsDBNull(4) ? "" : reader.GetString(4),
            IsActive = !reader.IsDBNull(5) && reader.GetInt32(5) == 1,
            MetadataJson = reader.IsDBNull(6) ? "{}" : reader.GetString(6),
            CreatedAt = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
        };
    }

    private static async Task<int> ExecuteScalarIntAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    private async Task<string> RunPythonScriptAsync(
        string scriptPath,
        string arguments,
        CancellationToken cancellationToken)
    {
        var pythonExecutable = CoachPythonRuntime.ResolvePythonExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            Arguments = $"\"{scriptPath}\" {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start python for {Path.GetFileName(scriptPath)}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("Coach python script failed: {Script} {Error}", scriptPath, stderr);
            throw new InvalidOperationException(
                $"Coach python script failed for {Path.GetFileName(scriptPath)}: {stderr.Trim()}");
        }

        return stdout.Trim();
    }

    private async Task<int> RedraftAllMomentsAsync(CancellationToken cancellationToken)
    {
        using var connection = _connectionFactory.CreateConnection();

        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = """
                DELETE FROM coach_inferences
                WHERE moment_id IN (
                    SELECT id
                    FROM coach_moments
                )
                """;
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = """
            SELECT
                m.id,
                m.player_id,
                COALESCE(m.note_text, ''),
                COALESCE(m.context_text, ''),
                COALESCE(m.champion, ''),
                COALESCE(m.role, ''),
                COALESCE(m.source_type, ''),
                COALESCE(b.objective_title, ''),
                COALESCE(m.game_time_s, 0),
                COALESCE(m.storyboard_path, ''),
                COALESCE(m.minimap_strip_path, '')
            FROM coach_moments m
            LEFT JOIN coach_objective_blocks b ON b.id = m.objective_block_id
            ORDER BY m.created_at DESC, m.id DESC
            """;

        var pending = new List<(long Id, long PlayerId, string NoteText, string ContextText, string Champion, string Role, string SourceType, string ObjectiveTitle, int GameTimeS, string StoryboardPath, string MinimapStripPath)>();
        using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetInt32(8),
                    reader.GetString(9),
                    reader.GetString(10)));
            }
        }

        var rescored = 0;
        foreach (var item in pending)
        {
            if (string.IsNullOrWhiteSpace(item.StoryboardPath)
                || string.IsNullOrWhiteSpace(item.MinimapStripPath)
                || !File.Exists(item.StoryboardPath)
                || !File.Exists(item.MinimapStripPath))
            {
                continue;
            }

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
            insertCmd.Parameters.AddWithValue("@playerId", item.PlayerId);
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
            rescored++;
        }

        return rescored;
    }

    private TrainingRuntimeState GetRuntimeStateSnapshot()
    {
        lock (_stateLock)
        {
            return _runtimeState with { };
        }
    }

    private void UpdateRuntimeState(string statusText)
    {
        lock (_stateLock)
        {
            if (!_runtimeState.IsTrainingInProgress)
            {
                return;
            }

            _runtimeState = _runtimeState with
            {
                ActiveTrainingStatusText = statusText
            };
        }
    }

    private void CompleteRuntimeState(CoachTrainResult result)
    {
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_stateLock)
        {
            _runtimeState = _runtimeState with
            {
                IsTrainingInProgress = false,
                ActiveTrainingKind = "",
                ActiveTrainingStatusText = "",
                ActiveTrainingStartedAt = null,
                LastTrainingSummary = result.Summary,
                LastTrainingCompletedAt = completedAt,
                LastTrainingSucceeded = true,
            };
        }
    }

    private void CompleteRuntimeState(Exception ex)
    {
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_stateLock)
        {
            _runtimeState = _runtimeState with
            {
                IsTrainingInProgress = false,
                ActiveTrainingKind = "",
                ActiveTrainingStatusText = "",
                ActiveTrainingStartedAt = null,
                LastTrainingSummary = $"Gemma coach training failed: {ex.Message}",
                LastTrainingCompletedAt = completedAt,
                LastTrainingSucceeded = false,
            };
        }
    }

    private sealed class GemmaRegisterPayload
    {
        public string ModelVersion { get; set; } = "";
    }

    private sealed class GemmaTrainPayload
    {
        public string ModelVersion { get; set; } = "";
        public string RegisteredModelVersion { get; set; } = "";
        public bool Trained { get; set; }
        public bool Registered { get; set; }
        public string Reason { get; set; } = "";
        public string ModelDirectory { get; set; } = "";
        public int TrainCount { get; set; }
        public int EvalCount { get; set; }
        public int GoldExamples { get; set; }
        public int SilverExamples { get; set; }
    }

    private sealed record TrainingRuntimeState
    {
        public bool IsTrainingInProgress { get; init; }
        public string ActiveTrainingKind { get; init; } = "";
        public string ActiveTrainingStatusText { get; init; } = "";
        public long? ActiveTrainingStartedAt { get; init; }
        public string LastTrainingSummary { get; init; } = "";
        public long? LastTrainingCompletedAt { get; init; }
        public bool LastTrainingSucceeded { get; init; }
    }
}

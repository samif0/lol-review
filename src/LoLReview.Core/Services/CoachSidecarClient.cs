#nullable enable

using System.Diagnostics;
using System.Text.Json;
using LoLReview.Core.Data;
using LoLReview.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

/// <summary>
/// Coach Lab draft labeling.
/// Prefers active personal/base/teacher models, then the premature prototype, then heuristics.
/// </summary>
public sealed class CoachSidecarClient : ICoachSidecarClient
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<CoachSidecarClient> _logger;

    public CoachSidecarClient(
        IDbConnectionFactory connectionFactory,
        ILogger<CoachSidecarClient> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<CoachDraftResult> DraftMomentAsync(
        CoachDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        var qwenDraft = await TryDraftWithQwenModelAsync(request, cancellationToken);
        if (qwenDraft is not null)
        {
            return qwenDraft;
        }

        var prototypeDraft = await TryDraftWithPrematurePrototypeAsync(request, cancellationToken);
        if (prototypeDraft is not null)
        {
            return prototypeDraft;
        }

        var objectiveKey = CoachDraftHeuristics.InferObjectiveKey(
            request.NoteText,
            request.ReviewContext,
            request.ActiveObjectiveTitle);

        var quality = CoachDraftHeuristics.InferMomentQuality(
            request.NoteText,
            request.ReviewContext);

        if (request.SourceType == "auto_sample" && string.IsNullOrWhiteSpace(request.NoteText))
        {
            quality = "neutral";
        }

        var primaryReason = string.IsNullOrWhiteSpace(objectiveKey)
            ? request.SourceType == "manual_clip" ? "manual_clip_review" : "lane_checkpoint"
            : objectiveKey;

        var confidence = request.SourceType == "manual_clip"
            ? 0.68
            : 0.35;

        if (!string.IsNullOrWhiteSpace(objectiveKey))
        {
            confidence += 0.1;
        }

        if (!string.IsNullOrWhiteSpace(request.NoteText))
        {
            confidence += 0.08;
        }

        confidence = Math.Clamp(confidence, 0.2, 0.9);

        var rationale = request.SourceType == "manual_clip"
            ? BuildManualClipRationale(quality, objectiveKey, request.NoteText)
            : BuildAutoSampleRationale(objectiveKey, request.ActiveObjectiveTitle);

        var rawPayload = JsonSerializer.Serialize(new
        {
            request.SourceType,
            quality,
            objectiveKey,
            primaryReason,
            confidence,
            note = request.NoteText,
            reviewContext = request.ReviewContext,
            activeObjective = request.ActiveObjectiveTitle
        });

        return new CoachDraftResult
        {
            ModelVersion = "assist-heuristic-v1",
            InferenceMode = "assist",
            MomentQuality = quality,
            PrimaryReason = primaryReason,
            ObjectiveKey = objectiveKey,
            Confidence = confidence,
            Rationale = rationale,
            RawPayload = rawPayload,
        };
    }

    private async Task<CoachDraftResult?> TryDraftWithQwenModelAsync(
        CoachDraftRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StoryboardPath) || !File.Exists(request.StoryboardPath))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.MinimapStripPath) || !File.Exists(request.MinimapStripPath))
        {
            return null;
        }

        ActiveCoachModel? model;
        using (var connection = _connectionFactory.CreateConnection())
        {
            model = await GetLatestActiveQwenModelAsync(connection, cancellationToken);
        }

        if (model is null || string.IsNullOrWhiteSpace(model.HfModelId))
        {
            return null;
        }

        try
        {
            var predictScript = CoachPythonRuntime.ResolveCoachLabScriptPath("predict_qwen_judge.py");
            var pythonExecutable = CoachPythonRuntime.ResolvePythonExecutable();
            var args = new List<string>
            {
                $"\"{predictScript}\"",
                $"--storyboard \"{request.StoryboardPath}\"",
                $"--minimap \"{request.MinimapStripPath}\"",
                $"--game-time-s {request.GameTimeS}",
                $"--champion \"{EscapeArg(request.Champion)}\"",
                $"--role \"{EscapeArg(request.Role)}\"",
                $"--active-objective-title \"{EscapeArg(request.ActiveObjectiveTitle)}\"",
                $"--source-type \"{EscapeArg(request.SourceType)}\"",
                $"--model-id \"{EscapeArg(model.HfModelId)}\"",
                $"--model-version \"{EscapeArg(model.ModelVersion)}\"",
                $"--mode {(model.ModelKind == "qwen_teacher" ? "teacher" : "judge")}",
            };

            if (!string.IsNullOrWhiteSpace(model.AdapterDirectory))
            {
                args.Add($"--adapter-dir \"{EscapeArg(model.AdapterDirectory)}\"");
            }

            if (model.ModelKind == "qwen_teacher")
            {
                args.Add($"--note-text \"{EscapeArg(request.NoteText)}\"");
                args.Add($"--review-context \"{EscapeArg(request.ReviewContext)}\"");
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                Arguments = string.Join(' ', args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(predictScript) ?? AppContext.BaseDirectory,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("{ModelKind} prediction failed: {Error}", model.ModelKind, stderr);
                return null;
            }

            var payload = JsonSerializer.Deserialize<PrematurePredictPayload>(stdout.Trim());
            if (payload is null)
            {
                return null;
            }

            return new CoachDraftResult
            {
                ModelVersion = payload.ModelVersion,
                InferenceMode = model.ModelKind,
                MomentQuality = payload.MomentQuality,
                PrimaryReason = payload.PrimaryReason,
                ObjectiveKey = payload.ObjectiveKey,
                Confidence = payload.Confidence,
                Rationale = payload.Rationale,
                RawPayload = stdout.Trim(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qwen-based coach prediction failed unexpectedly");
            return null;
        }
    }

    private async Task<CoachDraftResult?> TryDraftWithPrematurePrototypeAsync(
        CoachDraftRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StoryboardPath) || !File.Exists(request.StoryboardPath))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.MinimapStripPath) || !File.Exists(request.MinimapStripPath))
        {
            return null;
        }

        PrototypeModelInfo? model;
        using (var connection = _connectionFactory.CreateConnection())
        {
            model = await GetLatestActivePrototypeAsync(connection, cancellationToken);
        }

        if (model is null || string.IsNullOrWhiteSpace(model.ModelDirectory) || !Directory.Exists(model.ModelDirectory))
        {
            return null;
        }

        try
        {
            var predictScript = CoachPythonRuntime.ResolveCoachLabScriptPath("predict_premature_model.py");
            var pythonExecutable = CoachPythonRuntime.ResolvePythonExecutable();
            var psi = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                Arguments =
                    $"\"{predictScript}\" --model-dir \"{model.ModelDirectory}\" --storyboard \"{request.StoryboardPath}\" --minimap \"{request.MinimapStripPath}\" --game-time-s {request.GameTimeS}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(predictScript) ?? AppContext.BaseDirectory,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Premature prototype prediction failed: {Error}", stderr);
                return null;
            }

            var payload = JsonSerializer.Deserialize<PrematurePredictPayload>(stdout.Trim());
            if (payload is null)
            {
                return null;
            }

            return new CoachDraftResult
            {
                ModelVersion = payload.ModelVersion,
                InferenceMode = "premature_prototype",
                MomentQuality = payload.MomentQuality,
                PrimaryReason = payload.PrimaryReason,
                ObjectiveKey = payload.ObjectiveKey,
                Confidence = payload.Confidence,
                Rationale = payload.Rationale,
                RawPayload = stdout.Trim(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Premature prototype prediction failed unexpectedly");
            return null;
        }
    }

    private static string EscapeArg(string value) =>
        (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\"", "\\\"");

    private static async Task<PrototypeModelInfo?> GetLatestActivePrototypeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT model_version, metadata_json
            FROM coach_models
            WHERE model_kind = 'premature_prototype' AND is_active = 1
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var modelVersion = reader.IsDBNull(0) ? "" : reader.GetString(0);
        var metadataJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
        using var doc = JsonDocument.Parse(metadataJson);
        var modelDirectory = doc.RootElement.TryGetProperty("ModelDirectory", out var dirEl)
            ? dirEl.GetString() ?? ""
            : doc.RootElement.TryGetProperty("model_directory", out var snakeDir)
                ? snakeDir.GetString() ?? ""
                : "";

        return new PrototypeModelInfo
        {
            ModelVersion = modelVersion,
            ModelDirectory = modelDirectory
        };
    }

    private static async Task<ActiveCoachModel?> GetLatestActiveQwenModelAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var modelKind in new[] { "personal_adapter", "qwen_base", "qwen_teacher" })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT model_version, model_kind, metadata_json
                FROM coach_models
                WHERE model_kind = @modelKind AND is_active = 1
                ORDER BY created_at DESC, id DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@modelKind", modelKind);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                continue;
            }

            var modelVersion = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var metadataJson = reader.IsDBNull(2) ? "{}" : reader.GetString(2);
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            var hfModelId = root.TryGetProperty("hf_model_id", out var hfSnake)
                ? hfSnake.GetString() ?? ""
                : root.TryGetProperty("HfModelId", out var hfPascal)
                    ? hfPascal.GetString() ?? ""
                    : "";
            var adapterDirectory = root.TryGetProperty("adapter_dir", out var adapterSnake)
                ? adapterSnake.GetString() ?? ""
                : root.TryGetProperty("AdapterDirectory", out var adapterPascal)
                    ? adapterPascal.GetString() ?? ""
                    : "";

            return new ActiveCoachModel
            {
                ModelVersion = modelVersion,
                ModelKind = modelKind,
                HfModelId = hfModelId,
                AdapterDirectory = adapterDirectory
            };
        }

        return null;
    }

    private static string BuildManualClipRationale(string quality, string objectiveKey, string noteText)
    {
        if (!string.IsNullOrWhiteSpace(objectiveKey))
        {
            var objective = CoachObjectiveCatalog.Find(objectiveKey);
            var title = objective?.Title ?? objectiveKey;
            return $"Drafted as {quality} from the clip note. The note most strongly points at {title}.";
        }

        return string.IsNullOrWhiteSpace(noteText)
            ? "Manual clip without a note. Needs human normalization."
            : $"Drafted as {quality} from the clip note wording. Needs human normalization.";
    }

    private static string BuildAutoSampleRationale(string objectiveKey, string activeObjectiveTitle)
    {
        if (!string.IsNullOrWhiteSpace(objectiveKey))
        {
            var objective = CoachObjectiveCatalog.Find(objectiveKey);
            return $"Auto-sampled lane checkpoint near the current coaching theme: {objective?.Title ?? objectiveKey}. Needs review.";
        }

        return string.IsNullOrWhiteSpace(activeObjectiveTitle)
            ? "Auto-sampled lane checkpoint. Needs human review."
            : $"Auto-sampled lane checkpoint created while tracking the current objective \"{activeObjectiveTitle}\".";
    }

    private sealed class PrototypeModelInfo
    {
        public string ModelVersion { get; set; } = "";
        public string ModelDirectory { get; set; } = "";
    }

    private sealed class ActiveCoachModel
    {
        public string ModelVersion { get; set; } = "";
        public string ModelKind { get; set; } = "";
        public string HfModelId { get; set; } = "";
        public string AdapterDirectory { get; set; } = "";
    }

    private sealed class PrematurePredictPayload
    {
        public string ModelVersion { get; set; } = "";
        public string MomentQuality { get; set; } = "neutral";
        public string PrimaryReason { get; set; } = "";
        public string ObjectiveKey { get; set; } = "";
        public double Confidence { get; set; }
        public string Rationale { get; set; } = "";
    }
}

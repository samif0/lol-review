#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoLReview.Core.Data;
using LoLReview.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

/// <summary>
/// Coach Lab multimodal inference through a persistent Gemma worker.
/// Gemma is the only supported model family.
/// </summary>
public sealed class CoachSidecarClient : ICoachSidecarClient, IDisposable
{
    private const string WorkerProtocolPrefix = "__coach_json__";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<CoachSidecarClient> _logger;
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private GemmaWorkerSession? _worker;

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
        if (string.IsNullOrWhiteSpace(request.StoryboardPath) || !File.Exists(request.StoryboardPath))
        {
            throw new InvalidOperationException("Coach Lab could not find the storyboard artifact needed for Gemma drafting.");
        }

        if (string.IsNullOrWhiteSpace(request.MinimapStripPath) || !File.Exists(request.MinimapStripPath))
        {
            throw new InvalidOperationException("Coach Lab could not find the minimap artifact needed for Gemma drafting.");
        }

        var model = await RequireActiveGemmaModelAsync(cancellationToken);
        var response = await SendWorkerRequestAsync<GemmaDraftPayload>(
            model,
            new
            {
                command = "draft",
                storyboard = request.StoryboardPath,
                minimap = request.MinimapStripPath,
                game_time_s = request.GameTimeS,
                champion = request.Champion,
                role = request.Role,
                active_objective_title = request.ActiveObjectiveTitle,
                note_text = request.NoteText,
                review_context = request.ReviewContext,
                source_type = request.SourceType,
            },
            cancellationToken);

        return new CoachDraftResult
        {
            ModelVersion = response.ModelVersion,
            InferenceMode = "gemma",
            MomentQuality = response.MomentQuality,
            PrimaryReason = response.PrimaryReason,
            ObjectiveKey = response.ObjectiveKey,
            Confidence = response.Confidence,
            Rationale = response.Rationale,
            RawPayload = response.RawPayload,
        };
    }

    public async Task<CoachProblemsReport> AnalyzeProblemsAsync(
        CoachProblemAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = await RequireActiveGemmaModelAsync(cancellationToken);
        var response = await SendWorkerRequestAsync<GemmaProblemsPayload>(
            model,
            new
            {
                command = "problems",
                player_id = request.PlayerId,
                objective_block_id = request.ObjectiveBlockId,
                active_objective_title = request.ActiveObjectiveTitle,
                active_objective_key = request.ActiveObjectiveKey,
                review_context = request.ReviewContext,
                clip_cards = request.ClipCards,
            },
            cancellationToken);

        return new CoachProblemsReport
        {
            Title = response.Title,
            Summary = response.Summary,
            ModelVersion = response.ModelVersion,
            UsesTrainedModel = string.Equals(model.ModelKind, "gemma_adapter", StringComparison.OrdinalIgnoreCase),
            RawPayload = response.RawPayload,
            Problems = response.Problems,
        };
    }

    public async Task<CoachObjectiveSuggestion> PlanObjectiveAsync(
        CoachObjectivePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = await RequireActiveGemmaModelAsync(cancellationToken);
        var response = await SendWorkerRequestAsync<GemmaObjectivePlanPayload>(
            model,
            new
            {
                command = "objective_plan",
                player_id = request.PlayerId,
                objective_block_id = request.ObjectiveBlockId,
                active_objective_id = request.ActiveObjectiveId,
                active_objective_title = request.ActiveObjectiveTitle,
                active_objective_key = request.ActiveObjectiveKey,
                review_context = request.ReviewContext,
                clip_cards = request.ClipCards,
                candidates = request.Candidates,
            },
            cancellationToken);

        return new CoachObjectiveSuggestion
        {
            Title = response.Title,
            Summary = response.Summary,
            ModelVersion = response.ModelVersion,
            UsesTrainedModel = string.Equals(model.ModelKind, "gemma_adapter", StringComparison.OrdinalIgnoreCase),
            SuggestionMode = response.SuggestionMode,
            AttachedObjectiveId = response.AttachedObjectiveId,
            AttachedObjectiveTitle = response.AttachedObjectiveTitle,
            ObjectiveKey = response.ObjectiveKey,
            CandidateObjectiveTitle = response.CandidateObjectiveTitle,
            CandidateCompletionCriteria = response.CandidateCompletionCriteria,
            CandidateDescription = response.CandidateDescription,
            FollowUpMetric = response.FollowUpMetric,
            EvidenceMomentCount = response.EvidenceMomentCount,
            EvidenceGameCount = response.EvidenceGameCount,
            Confidence = response.Confidence,
            RawPayload = response.RawPayload,
            EvidenceClipIds = response.EvidenceClipIds,
        };
    }

    private async Task<ActiveCoachModel> RequireActiveGemmaModelAsync(CancellationToken cancellationToken)
    {
        using var connection = _connectionFactory.CreateConnection();
        var model = await GetLatestActiveGemmaModelAsync(connection, cancellationToken);
        if (model is not null)
        {
            return model;
        }

        throw new InvalidOperationException(
            "No active Gemma model is registered. Run Coach Lab training or register Gemma 4 E4B before using Gemma-only coach features.");
    }

    private static async Task<ActiveCoachModel?> GetLatestActiveGemmaModelAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var modelKind in new[] { "gemma_adapter", "gemma_base" })
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
                AdapterDirectory = adapterDirectory,
            };
        }

        return null;
    }

    private async Task<TPayload> SendWorkerRequestAsync<TPayload>(
        ActiveCoachModel model,
        object request,
        CancellationToken cancellationToken)
        where TPayload : GemmaWorkerPayloadBase
    {
        await _workerGate.WaitAsync(cancellationToken);
        try
        {
            var worker = await GetOrCreateWorkerAsync(model, cancellationToken);
            var requestJson = JsonSerializer.Serialize(request);
            await worker.Process.StandardInput.WriteLineAsync(requestJson);
            await worker.Process.StandardInput.FlushAsync();

            var payloadText = await ReadProtocolPayloadAsync(worker.Process.StandardOutput, cancellationToken);
            if (string.IsNullOrWhiteSpace(payloadText))
            {
                InvalidateWorkerUnsafe("Gemma worker exited without returning a response.");
                throw new InvalidOperationException("Gemma worker exited without returning a response.");
            }

            var payload = JsonSerializer.Deserialize<TPayload>(payloadText, JsonOptions)
                ?? throw new InvalidOperationException("Gemma worker returned unreadable JSON.");

            if (!payload.Ok)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(payload.Error)
                        ? "Gemma worker reported an unknown error."
                        : payload.Error);
            }

            payload.RawPayload = payloadText;
            return payload;
        }
        catch (OperationCanceledException)
        {
            InvalidateWorkerUnsafe("Gemma worker request was cancelled.");
            throw;
        }
        catch
        {
            InvalidateWorkerUnsafe("Gemma worker request failed and will be restarted.");
            throw;
        }
        finally
        {
            _workerGate.Release();
        }
    }

    private async Task<GemmaWorkerSession> GetOrCreateWorkerAsync(
        ActiveCoachModel model,
        CancellationToken cancellationToken)
    {
        var key = BuildWorkerKey(model);
        if (_worker is not null && !_worker.HasExited && string.Equals(_worker.Key, key, StringComparison.Ordinal))
        {
            return _worker;
        }

        InvalidateWorkerUnsafe(_worker is null ? null : "Gemma worker model selection changed. Restarting the worker.");
        _worker = await StartWorkerAsync(model, cancellationToken);
        return _worker;
    }

    private async Task<GemmaWorkerSession> StartWorkerAsync(
        ActiveCoachModel model,
        CancellationToken cancellationToken)
    {
        var workerScript = CoachPythonRuntime.ResolveCoachLabScriptPath("gemma_worker.py");
        var pythonExecutable = CoachPythonRuntime.ResolvePythonExecutable();
        var args = new List<string>
        {
            "-u",
            $"\"{workerScript}\"",
            $"--model-id \"{EscapeArg(model.HfModelId)}\"",
            $"--model-version \"{EscapeArg(model.ModelVersion)}\"",
        };

        if (!string.IsNullOrWhiteSpace(model.AdapterDirectory))
        {
            args.Add($"--adapter-dir \"{EscapeArg(model.AdapterDirectory)}\"");
        }

        var psi = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            Arguments = string.Join(' ', args),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(workerScript) ?? AppContext.BaseDirectory,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Gemma worker process.");

        var session = new GemmaWorkerSession(BuildWorkerKey(model), process);
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                return;
            }

            session.RememberErrorLine(eventArgs.Data);
            _logger.LogDebug("Gemma worker stderr: {Line}", eventArgs.Data);
        };
        process.BeginErrorReadLine();

        var readyText = await ReadProtocolPayloadAsync(process.StandardOutput, cancellationToken);
        if (string.IsNullOrWhiteSpace(readyText))
        {
            var stderr = session.GetRecentErrors();
            session.Dispose();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? "Gemma worker exited before it finished loading the model."
                    : $"Gemma worker exited before it finished loading the model. {stderr}");
        }

        var readyPayload = JsonSerializer.Deserialize<GemmaWorkerReadyPayload>(readyText, JsonOptions);
        if (readyPayload?.Ready != true)
        {
            var stderr = session.GetRecentErrors();
            session.Dispose();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? "Gemma worker did not report a ready state."
                    : $"Gemma worker did not report a ready state. {stderr}");
        }

        _logger.LogInformation(
            "Gemma worker loaded {ModelKind} model {ModelVersion} ({ModelId}).",
            model.ModelKind,
            model.ModelVersion,
            model.HfModelId);

        return session;
    }

    private async Task<string?> ReadProtocolPayloadAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (line.StartsWith(WorkerProtocolPrefix, StringComparison.Ordinal))
            {
                return line[WorkerProtocolPrefix.Length..];
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                _logger.LogDebug("Ignoring non-protocol stdout from Gemma worker: {Line}", line);
            }
        }
    }

    private void InvalidateWorkerUnsafe(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            _logger.LogInformation("{Reason}", reason);
        }

        _worker?.Dispose();
        _worker = null;
    }

    private static string BuildWorkerKey(ActiveCoachModel model) =>
        $"{model.ModelKind}|{model.ModelVersion}|{model.HfModelId}|{model.AdapterDirectory}";

    private static string EscapeArg(string value) =>
        (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\"", "\\\"");

    public void Dispose()
    {
        _workerGate.Wait();
        try
        {
            InvalidateWorkerUnsafe(null);
        }
        finally
        {
            _workerGate.Release();
            _workerGate.Dispose();
        }
    }

    private sealed class ActiveCoachModel
    {
        public string ModelVersion { get; set; } = "";
        public string ModelKind { get; set; } = "";
        public string HfModelId { get; set; } = "";
        public string AdapterDirectory { get; set; } = "";
    }

    private sealed class GemmaWorkerReadyPayload
    {
        [JsonPropertyName("ready")]
        public bool Ready { get; set; }
    }

    private abstract class GemmaWorkerPayloadBase
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; } = "";

        [JsonIgnore]
        public string RawPayload { get; set; } = "{}";
    }

    private sealed class GemmaDraftPayload : GemmaWorkerPayloadBase
    {
        public string ModelVersion { get; set; } = "";
        public string MomentQuality { get; set; } = "neutral";
        public string PrimaryReason { get; set; } = "";
        public string ObjectiveKey { get; set; } = "";
        public double Confidence { get; set; }
        public string Rationale { get; set; } = "";
    }

    private sealed class GemmaProblemsPayload : GemmaWorkerPayloadBase
    {
        public string ModelVersion { get; set; } = "";
        public string Title { get; set; } = "Recurring problems";
        public string Summary { get; set; } = "";
        public List<CoachProblemInsight> Problems { get; set; } = [];
    }

    private sealed class GemmaObjectivePlanPayload : GemmaWorkerPayloadBase
    {
        public string ModelVersion { get; set; } = "";
        public string Title { get; set; } = "Suggested objective";
        public string Summary { get; set; } = "";
        public string SuggestionMode { get; set; } = "";
        public long? AttachedObjectiveId { get; set; }
        public string AttachedObjectiveTitle { get; set; } = "";
        public string ObjectiveKey { get; set; } = "";
        public string CandidateObjectiveTitle { get; set; } = "";
        public string CandidateCompletionCriteria { get; set; } = "";
        public string CandidateDescription { get; set; } = "";
        public string FollowUpMetric { get; set; } = "";
        public int EvidenceMomentCount { get; set; }
        public int EvidenceGameCount { get; set; }
        public double Confidence { get; set; }
        public List<long> EvidenceClipIds { get; set; } = [];
    }

    private sealed class GemmaWorkerSession : IDisposable
    {
        private readonly ConcurrentQueue<string> _recentErrorLines = new();

        public GemmaWorkerSession(string key, Process process)
        {
            Key = key;
            Process = process;
            Process.StandardInput.AutoFlush = true;
        }

        public string Key { get; }

        public Process Process { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return Process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public void RememberErrorLine(string line)
        {
            _recentErrorLines.Enqueue(line);
            while (_recentErrorLines.Count > 20 && _recentErrorLines.TryDequeue(out _))
            {
            }
        }

        public string GetRecentErrors() =>
            string.Join(" | ", _recentErrorLines);

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    try
                    {
                        Process.StandardInput.WriteLine("{\"command\":\"shutdown\"}");
                        Process.StandardInput.Flush();
                    }
                    catch
                    {
                    }

                    Process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                Process.Dispose();
            }
        }
    }
}

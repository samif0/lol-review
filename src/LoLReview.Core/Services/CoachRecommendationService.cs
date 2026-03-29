#nullable enable

using System.Text;
using System.Text.Json;
using LoLReview.Core.Data;
using LoLReview.Core.Models;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Services;

public sealed class CoachRecommendationService : ICoachRecommendationService
{
    private readonly IDbConnectionFactory _connectionFactory;

    public CoachRecommendationService(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<CoachRecommendation> BuildAssistRecommendationAsync(
        long playerId,
        CoachObjectiveBlock block,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(NULLIF(l.objective_key, ''), NULLIF(i.objective_key, ''), ''),
                COUNT(*) AS moment_count,
                COUNT(DISTINCT m.game_id) AS game_count
            FROM coach_moments m
            LEFT JOIN coach_labels l ON l.moment_id = m.id
            LEFT JOIN coach_inferences i ON i.moment_id = m.id
            WHERE m.player_id = @playerId
              AND COALESCE(m.objective_block_id, @blockId) = @blockId
              AND COALESCE(NULLIF(l.label_quality, ''), i.moment_quality, 'neutral') = 'bad'
            GROUP BY COALESCE(NULLIF(l.objective_key, ''), NULLIF(i.objective_key, ''), '')
            ORDER BY game_count DESC, moment_count DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@playerId", playerId);
        cmd.Parameters.AddWithValue("@blockId", block.Id);

        string recommendationType = "keep";
        string title = "Assist mode active";
        string summary = "Keep saving lane clips with notes. The coach is still building clip-backed evidence.";
        string objectiveKey = block.ObjectiveKey;
        double confidence = 0.45;
        int evidenceGames = 0;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var blockerKey = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var momentCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            evidenceGames = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);

            if (!string.IsNullOrWhiteSpace(blockerKey) && evidenceGames >= 2)
            {
                recommendationType = "watch";
                objectiveKey = blockerKey;
                title = $"Watch: {CoachObjectiveCatalog.Find(blockerKey)?.Title ?? Humanize(blockerKey)}";
                summary = $"Repeated bad moments tied to this theme showed up in {evidenceGames} reviewed game(s) and {momentCount} moment(s). Assist mode is watching for a stable blocker before any stronger recommendation.";
                confidence = Math.Min(0.75, 0.45 + evidenceGames * 0.08);
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            block.ObjectiveKey,
            recommendationType,
            watchedObjective = objectiveKey,
            confidence,
            evidenceGames
        });

        return new CoachRecommendation
        {
            ObjectiveBlockId = block.Id,
            PlayerId = playerId,
            RecommendationType = recommendationType,
            State = "draft",
            ObjectiveKey = objectiveKey,
            Title = title,
            Summary = summary,
            Confidence = confidence,
            EvidenceGameCount = evidenceGames,
            RawPayload = payload,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    public async Task<CoachProblemsReport> BuildProblemsReportAsync(
        long playerId,
        CoachObjectiveBlock block,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var activePrototypeVersion = await GetActiveInferenceModelVersionAsync(connection, cancellationToken);
        var evidence = await LoadEvidenceRowsAsync(connection, playerId, block.Id, cancellationToken);
        var effective = evidence
            .Select(row => ToEffectiveEvidence(row, activePrototypeVersion))
            .Where(row => !string.IsNullOrWhiteSpace(row.MomentQuality))
            .ToList();

        if (string.IsNullOrWhiteSpace(activePrototypeVersion))
        {
            return new CoachProblemsReport
            {
                Title = "Train the coach first",
                Summary = "No coach model is active yet. Train or register one first, then ask for recurring problems.",
                ModelVersion = "assist-heuristic-v1",
                UsesTrainedModel = false,
            };
        }

        if (effective.Count == 0)
        {
            return new CoachProblemsReport
            {
                Title = "No scored clips yet",
                Summary = "The active coach model has not scored any saved moments yet. Train again or sync more clips first.",
                ModelVersion = activePrototypeVersion,
                UsesTrainedModel = true,
            };
        }

        var bad = effective
            .Where(row => string.Equals(row.MomentQuality, "bad", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (bad.Count == 0)
        {
            return new CoachProblemsReport
            {
                Title = "No clear problem yet",
                Summary = "The current scored clips do not show a strong recurring problem yet. Keep collecting lane clips before trusting this too much.",
                ModelVersion = activePrototypeVersion,
                UsesTrainedModel = true,
            };
        }

        var problems = bad
            .GroupBy(row => string.IsNullOrWhiteSpace(row.PrimaryReason) ? "manual_clip_review" : row.PrimaryReason, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CoachProblemInsight
            {
                ReasonKey = group.Key,
                Title = CoachObjectiveCatalog.Find(group.Key)?.Title ?? Humanize(group.Key),
                MomentCount = group.Count(),
                GameCount = group.Select(item => item.GameId).Distinct().Count(),
                Confidence = Math.Round(group.Average(item => item.Confidence), 2),
                ExampleNote = group.Select(item => item.ExampleText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "",
            })
            .OrderByDescending(problem => problem.GameCount)
            .ThenByDescending(problem => problem.MomentCount)
            .ThenByDescending(problem => problem.Confidence)
            .Take(3)
            .ToList();

        var summary = new StringBuilder();
        summary.AppendLine($"Scored clips inspected: {effective.Count}. Bad clips: {bad.Count}.");
        summary.AppendLine("Recurring problems:");
        foreach (var problem in problems)
        {
            summary.Append($"- {problem.Title}: {problem.MomentCount} clip(s) across {problem.GameCount} game(s)");
            if (problem.Confidence > 0)
            {
                summary.Append($", avg confidence {problem.Confidence:F2}");
            }

            summary.AppendLine(".");

            if (!string.IsNullOrWhiteSpace(problem.ExampleNote))
            {
                summary.AppendLine($"  Example: {TrimForSummary(problem.ExampleNote, 140)}");
            }
        }

        summary.AppendLine("Treat this as model-backed evidence, not final truth.");

        return new CoachProblemsReport
        {
            Title = "Recurring problems",
            Summary = summary.ToString().Trim(),
            ModelVersion = activePrototypeVersion,
            UsesTrainedModel = true,
            Problems = problems,
        };
    }

    public async Task<CoachObjectiveSuggestion> GenerateObjectiveSuggestionAsync(
        long playerId,
        CoachObjectiveBlock block,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var activePrototypeVersion = await GetActiveInferenceModelVersionAsync(connection, cancellationToken);
        var evidence = await LoadEvidenceRowsAsync(connection, playerId, block.Id, cancellationToken);
        var effective = evidence
            .Select(row => ToEffectiveEvidence(row, activePrototypeVersion))
            .Where(row => !string.IsNullOrWhiteSpace(row.MomentQuality))
            .ToList();

        if (string.IsNullOrWhiteSpace(activePrototypeVersion))
        {
            return new CoachObjectiveSuggestion
            {
                Title = "Train the coach first",
                Summary = "No coach model is active yet. Train or register one first, then ask for a suggested objective.",
                ModelVersion = "assist-heuristic-v1",
                UsesTrainedModel = false,
            };
        }

        if (effective.Count == 0)
        {
            return new CoachObjectiveSuggestion
            {
                Title = "No scored clips yet",
                Summary = "The active coach model has not scored any saved moments yet. Train again or sync more clips first.",
                ModelVersion = activePrototypeVersion,
                UsesTrainedModel = true,
            };
        }

        var bad = effective
            .Where(row => string.Equals(row.MomentQuality, "bad", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (bad.Count == 0)
        {
            return new CoachObjectiveSuggestion
            {
                Title = "Keep the current objective",
                Summary = string.IsNullOrWhiteSpace(block.ObjectiveTitle)
                    ? "The coach is not seeing a stronger blocker than your current focus yet."
                    : $"Stay on \"{block.ObjectiveTitle}\" for now. The current clips are not showing a stronger blocker than your current focus.",
                ModelVersion = activePrototypeVersion,
                UsesTrainedModel = true,
                SuggestionMode = "keep_current",
                AttachedObjectiveId = block.ObjectiveId,
                AttachedObjectiveTitle = block.ObjectiveTitle,
            };
        }

        var currentObjectiveEvidence = SummarizeObjectiveEvidence(
            bad.Where(row => MatchesObjective(row, block.ObjectiveId, block.ObjectiveTitle)),
            block.ObjectiveId,
            block.ObjectiveTitle);

        var existingObjectiveSuggestion = bad
            .Where(row =>
                (row.AttachedObjectiveId.HasValue || !string.IsNullOrWhiteSpace(row.AttachedObjectiveTitle))
                && !MatchesObjective(row, block.ObjectiveId, block.ObjectiveTitle))
            .GroupBy(
                row => row.AttachedObjectiveId?.ToString() ?? row.AttachedObjectiveTitle,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => SummarizeObjectiveEvidence(
                group,
                group.Select(item => item.AttachedObjectiveId).FirstOrDefault(id => id.HasValue),
                group.Select(item => item.AttachedObjectiveTitle).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? ""))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.GameCount)
            .ThenByDescending(item => item.MomentCount)
            .ThenByDescending(item => item.Confidence)
            .FirstOrDefault();

        var unattachedSuggestion = SummarizeReasonEvidence(bad.Where(row =>
            !row.AttachedObjectiveId.HasValue && string.IsNullOrWhiteSpace(row.AttachedObjectiveTitle)));

        var strongestExistingGameCount = existingObjectiveSuggestion?.GameCount ?? 0;
        var strongestExistingMomentCount = existingObjectiveSuggestion?.MomentCount ?? 0;
        var unattachedGameCount = unattachedSuggestion?.GameCount ?? 0;
        var unattachedMomentCount = unattachedSuggestion?.MomentCount ?? 0;

        if (currentObjectiveEvidence is not null
            && currentObjectiveEvidence.GameCount >= Math.Max(strongestExistingGameCount, unattachedGameCount)
            && currentObjectiveEvidence.MomentCount >= Math.Max(strongestExistingMomentCount, unattachedMomentCount))
        {
            return new CoachObjectiveSuggestion
            {
                Title = "Keep the current objective",
                Summary =
                    $"Stay on \"{currentObjectiveEvidence.AttachedObjectiveTitle}\" for now. The strongest bad clips still map back to your current objective.{Environment.NewLine}" +
                    $"Evidence: {currentObjectiveEvidence.MomentCount} bad clip(s) across {currentObjectiveEvidence.GameCount} game(s), avg confidence {currentObjectiveEvidence.Confidence:F2}.{Environment.NewLine}" +
                    $"Dominant problem theme: {CoachObjectiveCatalog.Find(currentObjectiveEvidence.TopReason)?.Title ?? Humanize(currentObjectiveEvidence.TopReason)}.",
                ModelVersion = activePrototypeVersion,
                UsesTrainedModel = true,
                SuggestionMode = "keep_current",
                AttachedObjectiveId = currentObjectiveEvidence.AttachedObjectiveId,
                AttachedObjectiveTitle = currentObjectiveEvidence.AttachedObjectiveTitle,
                ObjectiveKey = currentObjectiveEvidence.TopReason,
                EvidenceMomentCount = currentObjectiveEvidence.MomentCount,
                EvidenceGameCount = currentObjectiveEvidence.GameCount,
                Confidence = Math.Round(currentObjectiveEvidence.Confidence, 2),
            };
        }

        if (existingObjectiveSuggestion is not null
            && (existingObjectiveSuggestion.GameCount > unattachedGameCount
                || (existingObjectiveSuggestion.GameCount == unattachedGameCount
                    && existingObjectiveSuggestion.MomentCount >= unattachedMomentCount)))
        {
            return new CoachObjectiveSuggestion
            {
                Title = "Switch to an existing objective",
                Summary =
                    $"The strongest recurring problem already lines up with \"{existingObjectiveSuggestion.AttachedObjectiveTitle}\".{Environment.NewLine}" +
                    $"Evidence: {existingObjectiveSuggestion.MomentCount} bad clip(s) across {existingObjectiveSuggestion.GameCount} game(s), avg confidence {existingObjectiveSuggestion.Confidence:F2}.{Environment.NewLine}" +
                    $"Dominant problem theme: {CoachObjectiveCatalog.Find(existingObjectiveSuggestion.TopReason)?.Title ?? Humanize(existingObjectiveSuggestion.TopReason)}.",
                ModelVersion = activePrototypeVersion,
                UsesTrainedModel = true,
                SuggestionMode = "use_existing",
                AttachedObjectiveId = existingObjectiveSuggestion.AttachedObjectiveId,
                AttachedObjectiveTitle = existingObjectiveSuggestion.AttachedObjectiveTitle,
                ObjectiveKey = existingObjectiveSuggestion.TopReason,
                EvidenceMomentCount = existingObjectiveSuggestion.MomentCount,
                EvidenceGameCount = existingObjectiveSuggestion.GameCount,
                Confidence = Math.Round(existingObjectiveSuggestion.Confidence, 2),
            };
        }

        var topReason = unattachedSuggestion ?? SummarizeReasonEvidence(bad)!;
        var template = BuildObjectiveTemplate(topReason.ReasonKey, topReason.ExampleNote);

        return new CoachObjectiveSuggestion
        {
            Title = "Create a new objective",
            Summary =
                $"The strongest recurring problem is not attached to one of your current objectives yet.{Environment.NewLine}" +
                $"Suggested new objective: {template.Title}{Environment.NewLine}" +
                $"Evidence: {topReason.MomentCount} bad clip(s) across {topReason.GameCount} game(s), avg confidence {topReason.Confidence:F2}.{Environment.NewLine}" +
                $"Problem theme: {CoachObjectiveCatalog.Find(topReason.ReasonKey)?.Title ?? Humanize(topReason.ReasonKey)}.",
            ModelVersion = activePrototypeVersion,
            UsesTrainedModel = true,
            SuggestionMode = "create_new",
            ObjectiveKey = topReason.ReasonKey,
            CandidateObjectiveTitle = template.Title,
            CandidateCompletionCriteria = template.CompletionCriteria,
            CandidateDescription = template.Description,
            EvidenceMomentCount = topReason.MomentCount,
            EvidenceGameCount = topReason.GameCount,
            Confidence = Math.Round(topReason.Confidence, 2),
        };
    }

    private static async Task<string> GetActiveInferenceModelVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var modelKind in new[] { "personal_adapter", "qwen_base", "qwen_teacher", "premature_prototype" })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT model_version
                FROM coach_models
                WHERE model_kind = @modelKind AND is_active = 1
                ORDER BY created_at DESC, id DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@modelKind", modelKind);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            var version = result?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = """
                SELECT COUNT(*)
                FROM coach_inferences
                WHERE model_version = @modelVersion
                """;
            countCmd.Parameters.AddWithValue("@modelVersion", version);
            var inferenceCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);
            if (inferenceCount > 0)
            {
                return version;
            }
        }

        return "";
    }

    private static async Task<List<CoachEvidenceRow>> LoadEvidenceRowsAsync(
        SqliteConnection connection,
        long playerId,
        long blockId,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                m.id,
                m.game_id,
                COALESCE(m.note_text, ''),
                COALESCE(m.context_text, ''),
                COALESCE(l.label_quality, ''),
                COALESCE(l.primary_reason, ''),
                l.attached_objective_id,
                COALESCE(l.attached_objective_title, ''),
                COALESCE(i.moment_quality, ''),
                COALESCE(i.primary_reason, ''),
                COALESCE(i.objective_key, ''),
                i.attached_objective_id,
                COALESCE(i.attached_objective_title, ''),
                COALESCE(i.confidence, 0),
                COALESCE(i.model_version, ''),
                b.objective_id,
                COALESCE(b.objective_title, '')
            FROM coach_moments m
            LEFT JOIN coach_labels l ON l.moment_id = m.id
            LEFT JOIN coach_inferences i ON i.moment_id = m.id
            LEFT JOIN coach_objective_blocks b ON b.id = m.objective_block_id
            WHERE m.player_id = @playerId
              AND COALESCE(m.objective_block_id, @blockId) = @blockId
            ORDER BY m.created_at DESC, m.id DESC
            """;
        cmd.Parameters.AddWithValue("@playerId", playerId);
        cmd.Parameters.AddWithValue("@blockId", blockId);

        var rows = new List<CoachEvidenceRow>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CoachEvidenceRow
            {
                MomentId = reader.GetInt64(0),
                GameId = reader.GetInt64(1),
                NoteText = reader.GetString(2),
                ContextText = reader.GetString(3),
                LabelQuality = reader.GetString(4),
                LabelPrimaryReason = reader.GetString(5),
                LabelAttachedObjectiveId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                LabelAttachedObjectiveTitle = reader.GetString(7),
                InferenceQuality = reader.GetString(8),
                InferencePrimaryReason = reader.GetString(9),
                InferenceObjectiveKey = reader.GetString(10),
                InferenceAttachedObjectiveId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                InferenceAttachedObjectiveTitle = reader.GetString(12),
                InferenceConfidence = reader.IsDBNull(13) ? 0 : reader.GetDouble(13),
                InferenceModelVersion = reader.GetString(14),
                BlockObjectiveId = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                BlockObjectiveTitle = reader.GetString(16),
            });
        }

        return rows;
    }

    private static EffectiveEvidence ToEffectiveEvidence(CoachEvidenceRow row, string activePrototypeVersion)
    {
        var useActiveInference = !string.IsNullOrWhiteSpace(activePrototypeVersion)
            && string.Equals(row.InferenceModelVersion, activePrototypeVersion, StringComparison.OrdinalIgnoreCase);

        var quality = useActiveInference
            ? row.InferenceQuality
            : !string.IsNullOrWhiteSpace(row.LabelQuality)
                ? row.LabelQuality
                : row.InferenceQuality;

        var reason = useActiveInference
            ? !string.IsNullOrWhiteSpace(row.InferencePrimaryReason) ? row.InferencePrimaryReason : row.InferenceObjectiveKey
            : !string.IsNullOrWhiteSpace(row.LabelPrimaryReason)
                ? row.LabelPrimaryReason
                : !string.IsNullOrWhiteSpace(row.InferencePrimaryReason)
                    ? row.InferencePrimaryReason
                    : row.InferenceObjectiveKey;

        return new EffectiveEvidence
        {
            MomentId = row.MomentId,
            GameId = row.GameId,
            MomentQuality = string.IsNullOrWhiteSpace(quality) ? "neutral" : quality,
            PrimaryReason = string.IsNullOrWhiteSpace(reason) ? "manual_clip_review" : reason,
            AttachedObjectiveId = row.LabelAttachedObjectiveId ?? row.InferenceAttachedObjectiveId,
            AttachedObjectiveTitle =
                !string.IsNullOrWhiteSpace(row.LabelAttachedObjectiveTitle) ? row.LabelAttachedObjectiveTitle :
                row.InferenceAttachedObjectiveTitle,
            Confidence = useActiveInference
                ? Math.Clamp(row.InferenceConfidence, 0.05, 0.99)
                : !string.IsNullOrWhiteSpace(row.LabelQuality)
                    ? 0.95
                    : Math.Clamp(row.InferenceConfidence, 0.05, 0.99),
            UsesActiveModel = useActiveInference,
            ExampleText = !string.IsNullOrWhiteSpace(row.NoteText) ? row.NoteText : row.ContextText,
        };
    }

    private static bool MatchesObjective(EffectiveEvidence row, long? objectiveId, string? objectiveTitle)
    {
        if (objectiveId.HasValue && row.AttachedObjectiveId == objectiveId)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(objectiveTitle)
            && !string.IsNullOrWhiteSpace(row.AttachedObjectiveTitle)
            && string.Equals(row.AttachedObjectiveTitle, objectiveTitle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static ObjectiveEvidenceSummary? SummarizeObjectiveEvidence(
        IEnumerable<EffectiveEvidence> rows,
        long? objectiveId,
        string objectiveTitle)
    {
        var items = rows.ToList();
        if (items.Count == 0)
        {
            return null;
        }

        return new ObjectiveEvidenceSummary
        {
            AttachedObjectiveId = objectiveId,
            AttachedObjectiveTitle = objectiveTitle,
            MomentCount = items.Count,
            GameCount = items.Select(item => item.GameId).Distinct().Count(),
            Confidence = items.Average(item => item.Confidence),
            TopReason = items
                .GroupBy(item => string.IsNullOrWhiteSpace(item.PrimaryReason) ? "manual_clip_review" : item.PrimaryReason, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Select(item => item.GameId).Distinct().Count())
                .Select(group => group.Key)
                .FirstOrDefault() ?? "",
            ExampleNote = items.Select(item => item.ExampleText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "",
        };
    }

    private static ReasonEvidenceSummary? SummarizeReasonEvidence(IEnumerable<EffectiveEvidence> rows)
    {
        var items = rows.ToList();
        if (items.Count == 0)
        {
            return null;
        }

        return items
            .GroupBy(row => string.IsNullOrWhiteSpace(row.PrimaryReason) ? "manual_clip_review" : row.PrimaryReason, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ReasonEvidenceSummary
            {
                ReasonKey = group.Key,
                MomentCount = group.Count(),
                GameCount = group.Select(item => item.GameId).Distinct().Count(),
                Confidence = group.Average(item => item.Confidence),
                ExampleNote = group.Select(item => item.ExampleText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "",
            })
            .OrderByDescending(group => group.GameCount)
            .ThenByDescending(group => group.MomentCount)
            .ThenByDescending(group => group.Confidence)
            .FirstOrDefault();
    }

    private static ObjectiveTemplate BuildObjectiveTemplate(string reasonKey, string exampleNote)
    {
        var catalogItem = CoachObjectiveCatalog.Find(reasonKey);
        var title = catalogItem?.Title ?? Humanize(reasonKey);
        var description = catalogItem?.Summary
            ?? $"Focus on {title.ToLowerInvariant()} in lane clips.";

        var completionCriteria = reasonKey switch
        {
            "favorable_trade_windows" => "Across the next 5 reviewed games, avoid low-value lane trades into worse wave states.",
            "respect_jungle_support_threat" => "Across the next 5 reviewed games, stop stepping up when jungle or engage threat is missing.",
            "safe_lane_spacing" => "Across the next 5 reviewed games, keep a safer default lane spacing before commits.",
            "recall_on_crash_and_tempo" => "Across the next 5 reviewed games, reset on cleaner crashes instead of bleeding tempo.",
            "punish_enemy_cooldown_windows" => "Across the next 5 reviewed games, identify and use at least one enemy cooldown window in lane.",
            _ => "Use the next 5 reviewed games to produce cleaner clips around this theme.",
        };

        if (!string.IsNullOrWhiteSpace(exampleNote))
        {
            description = $"{description} Example evidence: {TrimForSummary(exampleNote, 120)}";
        }

        return new ObjectiveTemplate
        {
            Title = title,
            CompletionCriteria = completionCriteria,
            Description = description,
        };
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Needs Review";
        }

        return string.Join(' ',
            value.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string TrimForSummary(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..Math.Max(0, maxLength - 3)].TrimEnd()}...";
    }

    private sealed class CoachEvidenceRow
    {
        public long MomentId { get; set; }
        public long GameId { get; set; }
        public string NoteText { get; set; } = "";
        public string ContextText { get; set; } = "";
        public string LabelQuality { get; set; } = "";
        public string LabelPrimaryReason { get; set; } = "";
        public long? LabelAttachedObjectiveId { get; set; }
        public string LabelAttachedObjectiveTitle { get; set; } = "";
        public string InferenceQuality { get; set; } = "";
        public string InferencePrimaryReason { get; set; } = "";
        public string InferenceObjectiveKey { get; set; } = "";
        public long? InferenceAttachedObjectiveId { get; set; }
        public string InferenceAttachedObjectiveTitle { get; set; } = "";
        public double InferenceConfidence { get; set; }
        public string InferenceModelVersion { get; set; } = "";
        public long? BlockObjectiveId { get; set; }
        public string BlockObjectiveTitle { get; set; } = "";
    }

    private sealed class EffectiveEvidence
    {
        public long MomentId { get; set; }
        public long GameId { get; set; }
        public string MomentQuality { get; set; } = "neutral";
        public string PrimaryReason { get; set; } = "";
        public long? AttachedObjectiveId { get; set; }
        public string AttachedObjectiveTitle { get; set; } = "";
        public double Confidence { get; set; }
        public bool UsesActiveModel { get; set; }
        public string ExampleText { get; set; } = "";
    }

    private sealed class ObjectiveEvidenceSummary
    {
        public long? AttachedObjectiveId { get; set; }
        public string AttachedObjectiveTitle { get; set; } = "";
        public int MomentCount { get; set; }
        public int GameCount { get; set; }
        public double Confidence { get; set; }
        public string TopReason { get; set; } = "";
        public string ExampleNote { get; set; } = "";
    }

    private sealed class ReasonEvidenceSummary
    {
        public string ReasonKey { get; set; } = "";
        public int MomentCount { get; set; }
        public int GameCount { get; set; }
        public double Confidence { get; set; }
        public string ExampleNote { get; set; } = "";
    }

    private sealed class ObjectiveTemplate
    {
        public string Title { get; set; } = "";
        public string CompletionCriteria { get; set; } = "";
        public string Description { get; set; } = "";
    }
}

#nullable enable

using System.Text.Json;
using LoLReview.Core.Data;
using LoLReview.Core.Models;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Services;

public sealed class CoachRecommendationService : ICoachRecommendationService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICoachSidecarClient _sidecarClient;

    public CoachRecommendationService(
        IDbConnectionFactory connectionFactory,
        ICoachSidecarClient sidecarClient)
    {
        _connectionFactory = connectionFactory;
        _sidecarClient = sidecarClient;
    }

    public async Task<CoachRecommendation> BuildRecommendationAsync(
        long playerId,
        CoachObjectiveBlock block,
        CancellationToken cancellationToken = default)
    {
        var suggestion = await GenerateObjectiveSuggestionAsync(playerId, block, cancellationToken);
        return new CoachRecommendation
        {
            ObjectiveBlockId = block.Id,
            PlayerId = playerId,
            RecommendationType = NormalizeRecommendationType(suggestion.SuggestionMode),
            State = string.IsNullOrWhiteSpace(suggestion.SuggestionMode) ? "info" : "draft",
            ObjectiveKey = suggestion.ObjectiveKey,
            Title = suggestion.Title,
            Summary = suggestion.Summary,
            Confidence = suggestion.Confidence,
            EvidenceGameCount = suggestion.EvidenceGameCount,
            CandidateSnapshot = string.IsNullOrWhiteSpace(suggestion.RawPayload)
                ? JsonSerializer.Serialize(suggestion)
                : suggestion.RawPayload,
            AppliedObjectiveId = suggestion.AttachedObjectiveId,
            AppliedObjectiveTitle = suggestion.AttachedObjectiveTitle,
            EvaluationWindowGames = 5,
            RawPayload = string.IsNullOrWhiteSpace(suggestion.RawPayload)
                ? JsonSerializer.Serialize(suggestion)
                : suggestion.RawPayload,
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
        var activeModelVersion = await GetActiveInferenceModelVersionAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(activeModelVersion))
        {
            return new CoachProblemsReport
            {
                Title = "Gemma setup required",
                Summary = "No active Gemma model has scored Coach Lab moments yet. Register or train Gemma 4 E4B first.",
                ModelVersion = "",
                UsesTrainedModel = false,
            };
        }

        var evidence = await LoadEvidenceRowsAsync(connection, playerId, block.Id, activeModelVersion, cancellationToken);
        var effective = evidence
            .Select(row => ToEffectiveEvidence(row, activeModelVersion))
            .Where(row => !string.IsNullOrWhiteSpace(row.MomentQuality))
            .ToList();

        if (effective.Count == 0)
        {
            return new CoachProblemsReport
            {
                Title = "No scored clips yet",
                Summary = "The active Gemma model has not scored any saved moments yet. Re-sync or re-score moments after Gemma setup.",
                ModelVersion = activeModelVersion,
                UsesTrainedModel = false,
            };
        }

        try
        {
            return await _sidecarClient.AnalyzeProblemsAsync(
                new CoachProblemAnalysisRequest
                {
                    PlayerId = playerId,
                    ObjectiveBlockId = block.Id,
                    ActiveObjectiveTitle = block.ObjectiveTitle,
                    ActiveObjectiveKey = block.ObjectiveKey,
                    ReviewContext = BuildReviewContext(evidence),
                    ClipCards = BuildClipCards(effective),
                },
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return new CoachProblemsReport
            {
                Title = "Gemma setup required",
                Summary = ex.Message,
                ModelVersion = activeModelVersion,
                UsesTrainedModel = false,
            };
        }
    }

    public async Task<CoachObjectiveSuggestion> GenerateObjectiveSuggestionAsync(
        long playerId,
        CoachObjectiveBlock block,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var activeModelVersion = await GetActiveInferenceModelVersionAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(activeModelVersion))
        {
            return new CoachObjectiveSuggestion
            {
                Title = "Gemma setup required",
                Summary = "No active Gemma model has scored Coach Lab moments yet. Register or train Gemma 4 E4B first.",
                ModelVersion = "",
                UsesTrainedModel = false,
            };
        }

        var evidence = await LoadEvidenceRowsAsync(connection, playerId, block.Id, activeModelVersion, cancellationToken);
        var effective = evidence
            .Select(row => ToEffectiveEvidence(row, activeModelVersion))
            .Where(row => !string.IsNullOrWhiteSpace(row.MomentQuality))
            .ToList();

        if (effective.Count == 0)
        {
            return new CoachObjectiveSuggestion
            {
                Title = "No scored clips yet",
                Summary = "The active Gemma model has not scored any saved moments yet. Re-sync or re-score moments after Gemma setup.",
                ModelVersion = activeModelVersion,
                UsesTrainedModel = false,
            };
        }

        var candidates = BuildObjectiveCandidates(effective, block);

        try
        {
            return await _sidecarClient.PlanObjectiveAsync(
                new CoachObjectivePlanRequest
                {
                    PlayerId = playerId,
                    ObjectiveBlockId = block.Id,
                    ActiveObjectiveId = block.ObjectiveId,
                    ActiveObjectiveTitle = block.ObjectiveTitle,
                    ActiveObjectiveKey = block.ObjectiveKey,
                    ReviewContext = BuildReviewContext(evidence),
                    ClipCards = BuildClipCards(effective),
                    Candidates = candidates,
                },
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return new CoachObjectiveSuggestion
            {
                Title = "Gemma setup required",
                Summary = ex.Message,
                ModelVersion = activeModelVersion,
                UsesTrainedModel = false,
            };
        }
    }

    private static string NormalizeRecommendationType(string suggestionMode) =>
        suggestionMode switch
        {
            "keep_current" => "keep",
            "use_existing" => "use_existing",
            "create_new" => "create_new",
            _ => "info",
        };

    private static async Task<string> GetActiveInferenceModelVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var modelKind in new[] { "gemma_adapter", "gemma_base" })
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
        string activeModelVersion,
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
                COALESCE(b.objective_title, ''),
                COALESCE(g.review_notes, ''),
                COALESCE(g.mistakes, ''),
                COALESCE(g.focus_next, ''),
                COALESCE(g.spotted_problems, ''),
                COALESCE(m.created_at, 0)
            FROM coach_moments m
            LEFT JOIN coach_labels l ON l.moment_id = m.id
            LEFT JOIN coach_inferences i ON i.id = (
                SELECT i2.id
                FROM coach_inferences i2
                WHERE i2.moment_id = m.id
                  AND COALESCE(i2.model_version, '') = @activeModelVersion
                ORDER BY COALESCE(i2.updated_at, i2.created_at) DESC, i2.id DESC
                LIMIT 1
            )
            LEFT JOIN coach_objective_blocks b ON b.id = m.objective_block_id
            LEFT JOIN games g ON g.game_id = m.game_id
            WHERE m.player_id = @playerId
              AND COALESCE(m.objective_block_id, @blockId) = @blockId
            ORDER BY m.created_at DESC, m.id DESC
            LIMIT 20
            """;
        cmd.Parameters.AddWithValue("@playerId", playerId);
        cmd.Parameters.AddWithValue("@blockId", blockId);
        cmd.Parameters.AddWithValue("@activeModelVersion", activeModelVersion);

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
                BlockObjectiveTitle = reader.GetString(15),
                ReviewNotes = reader.GetString(16),
                Mistakes = reader.GetString(17),
                FocusNext = reader.GetString(18),
                SpottedProblems = reader.GetString(19),
                CreatedAt = reader.GetInt64(20),
            });
        }

        return rows;
    }

    private static EffectiveEvidence ToEffectiveEvidence(CoachEvidenceRow row, string activeModelVersion)
    {
        var hasActiveInference = string.Equals(
            row.InferenceModelVersion,
            activeModelVersion,
            StringComparison.OrdinalIgnoreCase);

        var quality = !string.IsNullOrWhiteSpace(row.LabelQuality)
            ? row.LabelQuality
            : hasActiveInference
                ? row.InferenceQuality
                : "";

        var reason = !string.IsNullOrWhiteSpace(row.LabelPrimaryReason)
            ? row.LabelPrimaryReason
            : hasActiveInference && !string.IsNullOrWhiteSpace(row.InferencePrimaryReason)
                ? row.InferencePrimaryReason
                : !string.IsNullOrWhiteSpace(row.NoteText)
                    ? row.NoteText
                    : BuildEvidenceText(row);

        return new EffectiveEvidence
        {
            MomentId = row.MomentId,
            GameId = row.GameId,
            MomentQuality = string.IsNullOrWhiteSpace(quality) ? "neutral" : quality,
            PrimaryReason = TrimForSummary(reason, 220),
            ObjectiveKey = CoachObjectiveCatalog.NormalizeKey(row.InferenceObjectiveKey),
            AttachedObjectiveId = row.LabelAttachedObjectiveId ?? row.InferenceAttachedObjectiveId,
            AttachedObjectiveTitle =
                !string.IsNullOrWhiteSpace(row.LabelAttachedObjectiveTitle) ? row.LabelAttachedObjectiveTitle :
                row.InferenceAttachedObjectiveTitle,
            Confidence = !string.IsNullOrWhiteSpace(row.LabelQuality)
                ? 0.95
                : hasActiveInference
                    ? Math.Clamp(row.InferenceConfidence, 0.05, 0.99)
                    : 0.0,
            ExampleText = BuildEvidenceText(row),
            CreatedAt = row.CreatedAt,
        };
    }

    private static string BuildEvidenceText(CoachEvidenceRow row)
    {
        foreach (var text in new[]
                 {
                     row.NoteText,
                     row.ContextText,
                     row.Mistakes,
                     row.FocusNext,
                     row.ReviewNotes,
                     row.SpottedProblems,
                 })
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                return TrimForSummary(text, 220);
            }
        }

        return "";
    }

    private static string BuildReviewContext(IEnumerable<CoachEvidenceRow> rows)
    {
        return string.Join(
            " | ",
            rows.SelectMany(row => new[]
                {
                    row.ReviewNotes,
                    row.Mistakes,
                    row.FocusNext,
                    row.SpottedProblems,
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8));
    }

    private static List<CoachClipCard> BuildClipCards(IEnumerable<EffectiveEvidence> rows)
    {
        return rows
            .OrderByDescending(row => row.CreatedAt)
            .Take(12)
            .Select(row => new CoachClipCard
            {
                MomentId = row.MomentId,
                GameId = row.GameId,
                MomentQuality = row.MomentQuality,
                ReasonKey = string.IsNullOrWhiteSpace(row.PrimaryReason) ? row.ExampleText : row.PrimaryReason,
                ObjectiveKey = row.ObjectiveKey,
                Confidence = Math.Round(row.Confidence, 2),
                Evidence = row.ExampleText,
                AttachedObjectiveId = row.AttachedObjectiveId,
                AttachedObjectiveTitle = row.AttachedObjectiveTitle,
            })
            .ToList();
    }

    private static List<CoachObjectiveCandidate> BuildObjectiveCandidates(
        IReadOnlyList<EffectiveEvidence> effective,
        CoachObjectiveBlock block)
    {
        var bad = effective
            .Where(row => string.Equals(row.MomentQuality, "bad", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = new List<CoachObjectiveCandidate>
        {
            new()
            {
                CandidateKey = "keep_current",
                CandidateType = "keep_current",
                ObjectiveId = block.ObjectiveId,
                ObjectiveKey = block.ObjectiveKey,
                Title = string.IsNullOrWhiteSpace(block.ObjectiveTitle) ? "Keep current objective" : block.ObjectiveTitle,
                Description = "Stay on the current objective unless another recurring blocker is clearly stronger.",
            }
        };

        var existing = bad
            .Where(row => row.AttachedObjectiveId.HasValue || !string.IsNullOrWhiteSpace(row.AttachedObjectiveTitle))
            .Where(row =>
                row.AttachedObjectiveId != block.ObjectiveId
                && !string.Equals(row.AttachedObjectiveTitle, block.ObjectiveTitle, StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.AttachedObjectiveId?.ToString() ?? row.AttachedObjectiveTitle, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Select(item => item.GameId).Distinct().Count())
            .ThenByDescending(group => group.Count())
            .Take(3);

        foreach (var group in existing)
        {
            var first = group.First();
            candidates.Add(new CoachObjectiveCandidate
            {
                CandidateKey = $"existing:{first.AttachedObjectiveId?.ToString() ?? first.AttachedObjectiveTitle}",
                CandidateType = "use_existing",
                ObjectiveId = first.AttachedObjectiveId,
                ObjectiveKey = first.ObjectiveKey,
                Title = !string.IsNullOrWhiteSpace(first.AttachedObjectiveTitle)
                    ? first.AttachedObjectiveTitle
                    : CoachObjectiveCatalog.Find(first.ObjectiveKey)?.Title ?? Humanize(first.ObjectiveKey),
                Description = $"Observed in {group.Count()} bad clip(s) across {group.Select(item => item.GameId).Distinct().Count()} reviewed game(s).",
            });
        }

        if (bad.Count > 0)
        {
            var sampleNotes = bad
                .Select(row => string.IsNullOrWhiteSpace(row.PrimaryReason) ? row.ExampleText : row.PrimaryReason)
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(note => TrimForSummary(note, 90))
                .ToList();

            var description = $"Synthesize a new objective directly from {bad.Count} recent bad clip(s).";
            if (sampleNotes.Count > 0)
            {
                description += $" Example evidence: {string.Join(" | ", sampleNotes)}";
            }

            candidates.Add(new CoachObjectiveCandidate
            {
                CandidateKey = "new:dynamic",
                CandidateType = "create_new",
                ObjectiveKey = "",
                Title = "Create a new objective from recent clips",
                Description = description,
                CompletionCriteria = "Across the next 5 reviewed games, reduce recurrence of the same issue described in the cited clips.",
            });
        }

        return candidates;
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
        public string BlockObjectiveTitle { get; set; } = "";
        public string ReviewNotes { get; set; } = "";
        public string Mistakes { get; set; } = "";
        public string FocusNext { get; set; } = "";
        public string SpottedProblems { get; set; } = "";
        public long CreatedAt { get; set; }
    }

    private sealed class EffectiveEvidence
    {
        public long MomentId { get; set; }
        public long GameId { get; set; }
        public string MomentQuality { get; set; } = "neutral";
        public string PrimaryReason { get; set; } = "";
        public string ObjectiveKey { get; set; } = "";
        public long? AttachedObjectiveId { get; set; }
        public string AttachedObjectiveTitle { get; set; } = "";
        public double Confidence { get; set; }
        public string ExampleText { get; set; } = "";
        public long CreatedAt { get; set; }
    }
}

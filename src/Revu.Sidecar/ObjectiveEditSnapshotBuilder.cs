#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Builds the full edit-hydration snapshot served at GET /api/objective?id=N.
///
/// <para>
/// Reproduces <c>ObjectivesViewModel.BeginEditObjectiveAsync</c>'s hydration: the
/// objective's core fields + multi-phase practice flags + structured criterion +
/// focus-phase + custom prompts + champion gate, plus the played-champion list the
/// picker uses for typeahead. Every picker index/key matches the WinUI form so the
/// frontend can echo them straight back into POST /api/objective/create|update.
/// </para>
///
/// <para>
/// READ-ONLY: only repo read methods are called
/// (<see cref="IObjectivesRepository.GetAsync"/>,
/// <see cref="IObjectivesRepository.GetChampionsForObjectiveAsync"/>,
/// <see cref="IObjectivesRepository.GetPlayedChampionsAsync"/>,
/// <see cref="IPromptsRepository.GetPromptsForObjectiveAsync"/>). No
/// writes/migrations. The actual save is the WRITE path (Program.cs POST handlers).
/// Each lookup is individually try/catch-guarded so a degraded sub-query never
/// blanks the whole form — the picker falls back to manual entry, etc.
/// </para>
/// </summary>
public sealed class ObjectiveEditSnapshotBuilder
{
    // Matches LoadChampionSuggestionsAsync / AllChampionNames in the WinUI VM.
    private const int PlayedChampionsTake = 200;

    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IPromptsRepository _promptsRepo;
    private readonly ILogger<ObjectiveEditSnapshotBuilder> _logger;

    public ObjectiveEditSnapshotBuilder(
        IObjectivesRepository objectivesRepo,
        IPromptsRepository promptsRepo,
        ILogger<ObjectiveEditSnapshotBuilder> logger)
    {
        _objectivesRepo = objectivesRepo;
        _promptsRepo = promptsRepo;
        _logger = logger;
    }

    /// <summary>
    /// Hydrate one objective for the Edit form. Returns null when the id doesn't
    /// resolve (the endpoint maps that to 404).
    /// </summary>
    public async Task<ObjectiveEditDto?> BuildAsync(long objectiveId, CancellationToken ct = default)
    {
        ObjectiveSummary? obj;
        try
        {
            obj = await _objectivesRepo.GetAsync(objectiveId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objective edit: GetAsync failed for {ObjectiveId}", objectiveId);
            return null;
        }

        if (obj is null)
        {
            return null;
        }

        var isActive = string.Equals(obj.Status, "active", StringComparison.OrdinalIgnoreCase);

        var typeIndex = string.Equals(obj.Type, "mini", StringComparison.OrdinalIgnoreCase) ? 2
                      : string.Equals(obj.Type, "mental", StringComparison.OrdinalIgnoreCase) ? 1
                      : 0;

        // Mirror BeginEditObjectiveAsync: default a never-set mini target to 3.
        var targetGameCount = obj.TargetGameCount > 0 ? obj.TargetGameCount : 3;

        var focusPhaseIndex = ObjectiveFocusPhases.ToIndex(obj.FocusPhase);
        var criteriaMetricIndex = CriteriaMetricIndexFromKey(obj.CriteriaMetric);
        var criteriaOpIndex = obj.CriteriaOp == ObjectiveCriteria.OpAtMost ? 1 : 0;
        var criteriaValueText = obj.HasStructuredCriteria
            ? ObjectiveCriteria.FormatValue(obj.CriteriaValue)
            : "";

        var prompts = await SafePromptsAsync(objectiveId);
        var champions = await SafeChampionsAsync(objectiveId);
        var playedChampions = await SafePlayedChampionsAsync();

        return new ObjectiveEditDto(
            Id: obj.Id,
            Title: obj.Title,
            SkillArea: obj.SkillArea,
            Type: obj.Type,
            TypeIndex: typeIndex,
            Status: obj.Status,
            IsActive: isActive,
            CompletionCriteria: obj.CompletionCriteria,
            Description: obj.Description,
            PracticePre: obj.PracticePre,
            PracticeIn: obj.PracticeIn,
            PracticePost: obj.PracticePost,
            TargetGameCount: targetGameCount,
            FocusPhaseIndex: focusPhaseIndex,
            CriteriaMetricIndex: criteriaMetricIndex,
            CriteriaOpIndex: criteriaOpIndex,
            CriteriaValueText: criteriaValueText,
            Prompts: prompts,
            Champions: champions,
            PlayedChampions: playedChampions);
    }

    private async Task<IReadOnlyList<PromptDraftDto>> SafePromptsAsync(long objectiveId)
    {
        try
        {
            var prompts = await _promptsRepo.GetPromptsForObjectiveAsync(objectiveId);
            return prompts
                .OrderBy(p => p.SortOrder)
                .Select(p => new PromptDraftDto(
                    Id: p.Id,
                    Phase: ObjectivePhases.Normalize(p.Phase),
                    PhaseIndex: ObjectivePhases.ToIndex(p.Phase),
                    Label: p.Label))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objective edit: prompt load failed for {ObjectiveId}", objectiveId);
            return Array.Empty<PromptDraftDto>();
        }
    }

    private async Task<IReadOnlyList<string>> SafeChampionsAsync(long objectiveId)
    {
        try
        {
            return await _objectivesRepo.GetChampionsForObjectiveAsync(objectiveId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objective edit: champion gate load failed for {ObjectiveId}", objectiveId);
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<string>> SafePlayedChampionsAsync()
    {
        try
        {
            return await _objectivesRepo.GetPlayedChampionsAsync(PlayedChampionsTake);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objective edit: played-champion typeahead load failed");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The criteria-metric picker options (index 0 = "Free text only", then the
    /// declared metrics). Shared by the create form too — exposed as a static so
    /// the create endpoint can surface it without a separate builder.
    /// </summary>
    public static IReadOnlyList<CriteriaMetricOptionDto> BuildCriteriaMetricOptions()
    {
        var options = new List<CriteriaMetricOptionDto>
        {
            new(Index: 0, Key: "", Label: "Free text only", LowerIsBetter: false),
        };
        var metrics = ObjectiveCriteria.Metrics;
        for (var i = 0; i < metrics.Count; i++)
        {
            options.Add(new CriteriaMetricOptionDto(
                Index: i + 1,
                Key: metrics[i].Key,
                Label: metrics[i].Label,
                LowerIsBetter: metrics[i].LowerIsBetter));
        }
        return options;
    }

    /// <summary>
    /// Map a persisted criteria-metric key to its picker index (0 = free text).
    /// Mirrors ObjectivesViewModel.CriteriaMetricIndexFromKey.
    /// </summary>
    private static int CriteriaMetricIndexFromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return 0;
        }

        var metrics = ObjectiveCriteria.Metrics;
        for (var i = 0; i < metrics.Count; i++)
        {
            if (string.Equals(metrics[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }
}

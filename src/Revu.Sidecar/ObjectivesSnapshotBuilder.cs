#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only objectives snapshot served at GET /api/objectives.
///
/// <para>
/// Reproduces <c>ObjectivesViewModel.RefreshDataAsync</c>'s data loading +
/// formatting, minus all WinUI/dispatcher concerns, and emits the camelCase JSON
/// contract (see <see cref="ObjectivesDto"/> and desktop/ui/sample-objectives.json).
/// It deliberately does NOT reference the WinUI ViewModel — only the Core repo
/// interfaces.
/// </para>
///
/// <para>
/// READ-ONLY ABSOLUTE: only repo read methods are called
/// (<see cref="IObjectivesRepository.GetAllAsync"/>,
/// <see cref="IObjectivesRepository.GetScoreHistoryAsync"/>,
/// <see cref="IObjectivesRepository.GetChampionsForObjectiveAsync"/>,
/// <see cref="IGameAnalyticsQuery.GetRecentSpottedProblemsAsync"/>, and the
/// static <see cref="IObjectivesRepository.GetLevelInfo"/>). No writes/migrations.
/// The create-objective form + every mutation are DEFERRED: the contract carries
/// a disabled <see cref="CreateFormDto"/> the frontend renders-but-disables.
/// TODO: wire the create/edit/complete/delete mutations once a write-capable
/// sidecar phase lands.
/// </para>
///
/// <para>
/// Like the dashboard builder, each section is wrapped in try/catch that degrades
/// to empty so one failing query never blanks the whole page. Per-objective
/// score-history / champion lookups are individually guarded so one bad objective
/// doesn't drop the rest.
/// </para>
///
/// <para>
/// COLOR PARITY: mirrors <see cref="DashboardSnapshotBuilder"/>'s hardcoded
/// glass-aurora palette + objective level ramp. The WinUI app sources these from
/// <c>Revu.App.Styling.AppSemanticPalette</c> (not visible to Core). For minis we
/// use the gold accent like the dashboard does. TODO: lift these constants into
/// Revu.Core so the app and both sidecar builders share one source of truth.
/// </para>
/// </summary>
public sealed class ObjectivesSnapshotBuilder
{
    private const int SpottedProblemsTake = 12;
    private const int ScoreHistoryTake = 20;

    // ── Mockup palette (TODO: extract to Revu.Core; mirror DashboardSnapshotBuilder) ─
    private const string GoldHex = "#f3c794";
    private const string WinHex = "#8ee7ba";
    private const string LossHex = "#f3a3a8";
    private const string RingTrackHex = "rgba(255,255,255,0.13)";

    // Criteria hit-rate chip palette — mirrors ObjectiveDisplayItem.CriteriaHitRateBrush
    // (AppSemanticPalette MutedText/Positive/Negative). Positive/Negative reuse the
    // win/loss hexes; muted is the dim-text hex.
    private const string PositiveHex = WinHex;
    private const string NegativeHex = LossHex;
    private const string MutedTextHex = "#a79ec2";

    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IGameAnalyticsQuery _gameAnalytics;
    private readonly IConfigService _config;
    private readonly ILogger<ObjectivesSnapshotBuilder> _logger;

    public ObjectivesSnapshotBuilder(
        IObjectivesRepository objectivesRepo,
        IGameAnalyticsQuery gameAnalytics,
        IConfigService config,
        ILogger<ObjectivesSnapshotBuilder> logger)
    {
        _objectivesRepo = objectivesRepo;
        _gameAnalytics = gameAnalytics;
        _config = config;
        _logger = logger;
    }

    public async Task<ObjectivesDto> BuildAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;

        var active = new List<ObjectiveCardDto>();
        var focus = new List<ObjectiveCardDto>();
        var completed = new List<CompletedObjectiveDto>();

        // ── Objectives (active + completed) ─────────────────────────────────
        try
        {
            // Mirror the VM: GetAllAsync then split on Status, so completed and
            // active share one query and one ordering.
            var allObjectives = await _objectivesRepo.GetAllAsync();
            foreach (var obj in allObjectives)
            {
                if (string.Equals(obj.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    var card = await BuildActiveCardAsync(obj);
                    active.Add(card);
                    if (card.IsMini)
                    {
                        focus.Add(card);
                    }
                }
                else
                {
                    completed.Add(new CompletedObjectiveDto(
                        Id: obj.Id,
                        Title: obj.Title,
                        PhaseLabel: ObjectivePhases.ToDisplayLabel(obj.Phase),
                        Score: obj.Score,
                        GameCount: obj.GameCount,
                        SummaryText: $"{obj.Score} pts  •  {obj.GameCount} games"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objectives: active/completed load failed");
        }

        // ── Recent spotted problems (backlog context) ───────────────────────
        var spotted = await BuildSpottedProblemsAsync();

        // ── Create-form picker data: played-champion typeahead + criteria metrics.
        //    EDIT hydrates these per-objective via GET /api/objective?id=N; the
        //    NEW form needs them up front. Guarded — the picker degrades gracefully.
        var playedChampions = await SafePlayedChampionsAsync();
        var criteriaMetrics = ObjectiveEditSnapshotBuilder.BuildCriteriaMetricOptions();

        var hasActive = active.Count > 0;
        var hasCompleted = completed.Count > 0;
        var hasFocus = focus.Count > 0;
        var hasSpotted = spotted.Count > 0;

        return new ObjectivesDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            HasObjectives: hasActive || hasCompleted,
            HasActiveObjectives: hasActive,
            HasCompletedObjectives: hasCompleted,
            HasFocusObjectives: hasFocus,
            HasSpottedProblems: hasSpotted,
            ActiveObjectives: active,
            FocusObjectives: focus,
            CompletedObjectives: completed,
            SpottedProblems: spotted,
            CreateForm: BuildCreateForm(),
            PlayedChampions: playedChampions,
            CriteriaMetrics: criteriaMetrics);
    }

    private async Task<IReadOnlyList<string>> SafePlayedChampionsAsync()
    {
        try
        {
            // 200 mirrors AllChampionNames in the WinUI VM (typeahead source).
            return await _objectivesRepo.GetPlayedChampionsAsync(200);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objectives: played-champion typeahead load failed");
            return Array.Empty<string>();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Active objective card (mirror ObjectivesViewModel + ObjectiveDisplayItem)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ObjectiveCardDto> BuildActiveCardAsync(ObjectiveSummary obj)
    {
        var info = IObjectivesRepository.GetLevelInfo(obj.Score, obj.GameCount);
        var isMini = obj.IsMini;
        var isMental = string.Equals(obj.Type, "mental", StringComparison.OrdinalIgnoreCase);

        // Per-objective lookups are individually guarded so one bad row doesn't
        // drop the rest. Degrade to empty.
        var scoreHistory = await SafeScoreHistoryAsync(obj.Id);
        var champions = await SafeChampionsAsync(obj.Id);

        // ── Measured-criterion hit rate (mirror ObjectiveDisplayItem) ────────
        // Only query when the objective actually carries a structured criterion;
        // free-text-only objectives report (0,0). Guarded so a bad row degrades.
        var hasStructuredCriteria = obj.HasStructuredCriteria;
        var (criteriaHits, criteriaEvaluated) = hasStructuredCriteria
            ? await SafeCriteriaHitRateAsync(obj.Id)
            : (0, 0);

        var criteriaHitRateText = !hasStructuredCriteria
            ? ""
            : criteriaEvaluated > 0
                ? $"HIT {criteriaHits}/{criteriaEvaluated} GAMES"
                : "NOT MEASURED YET";

        var criteriaHitRateHex = criteriaEvaluated == 0
            ? MutedTextHex
            : criteriaHits * 2 >= criteriaEvaluated
                ? PositiveHex
                : NegativeHex;

        var criteriaText = hasStructuredCriteria
            ? $"Success: {Revu.Core.Services.ObjectiveCriteria.Describe(obj.CriteriaMetric, obj.CriteriaOp, obj.CriteriaValue)}"
            : (string.IsNullOrWhiteSpace(obj.CompletionCriteria) ? "" : $"Success: {obj.CompletionCriteria}");
        var hasCriteriaText = !string.IsNullOrWhiteSpace(criteriaText);

        // Minis fill by games done; mastery/mental objectives by score arc
        // (mirror ObjectiveDisplayItem.DisplayProgress).
        var progress = isMini && obj.TargetGameCount > 0
            ? Math.Clamp((double)obj.GameCount / obj.TargetGameCount, 0.0, 1.0)
            : info.Progress;

        var levelColorHex = isMini ? GoldHex : ObjectiveLevelHex(info.LevelIndex);
        var levelDimColorHex = isMini ? RingTrackHex : ObjectiveLevelDimHex(info.LevelIndex);

        var phaseLabel = ObjectivePhases.ToDisplayLabel(obj.Phase);
        var phasesSummary = BuildPhasesSummary(obj.PracticePre, obj.PracticeIn, obj.PracticePost, phaseLabel);
        var levelName = isMini ? "" : info.LevelName;

        var infoText = isMini
            ? (obj.TargetGameCount > 0
                ? $"FOCUS DRILL  •  {Math.Min(obj.GameCount, obj.TargetGameCount)} of {obj.TargetGameCount} games"
                : $"FOCUS DRILL  •  {obj.GameCount} games")
            : $"{info.LevelName}  •  {obj.Score} pts  •  {obj.GameCount} games";

        var metaText = BuildObjectiveMetaText(isMini, obj.TargetGameCount, obj.GameCount, phaseLabel, levelName, obj.Score);

        var focusProgressText = isMini && obj.TargetGameCount > 0
            ? $"{Math.Min(obj.GameCount, obj.TargetGameCount)} of {obj.TargetGameCount} games"
            : "";

        var championsSummary = champions.Count > 0
            ? string.Join(", ", champions).ToUpperInvariant()
            : "ALL CHAMPIONS";

        return new ObjectiveCardDto(
            Id: obj.Id,
            Title: obj.Title,
            SkillArea: obj.SkillArea,
            Type: obj.Type,
            PhaseLabel: phaseLabel,
            PhasesSummary: phasesSummary,
            IsMini: isMini,
            IsMental: isMental,
            Score: obj.Score,
            GameCount: obj.GameCount,
            TargetGameCount: obj.TargetGameCount,
            Progress: progress,
            LevelName: levelName,
            LevelColorHex: levelColorHex,
            LevelDimColorHex: levelDimColorHex,
            IsPriority: obj.IsPriority,
            InfoText: infoText,
            ScoreHistory: scoreHistory,
            HasScoreHistory: scoreHistory.Count >= 2,
            Champions: champions,
            ChampionsSummary: championsSummary,
            MetaText: metaText,
            FocusProgressText: focusProgressText,
            HasStructuredCriteria: hasStructuredCriteria,
            CriteriaHits: criteriaHits,
            CriteriaEvaluated: criteriaEvaluated,
            CriteriaHitRateText: criteriaHitRateText,
            CriteriaHitRateHex: criteriaHitRateHex,
            CriteriaText: criteriaText,
            HasCriteriaText: hasCriteriaText);
    }

    private async Task<(int Hits, int Evaluated)> SafeCriteriaHitRateAsync(long objectiveId)
    {
        try
        {
            return await _objectivesRepo.GetCriteriaHitRateAsync(objectiveId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objectives: criteria hit-rate load failed for {ObjectiveId}", objectiveId);
            return (0, 0);
        }
    }

    private async Task<IReadOnlyList<int>> SafeScoreHistoryAsync(long objectiveId)
    {
        try
        {
            return await _objectivesRepo.GetScoreHistoryAsync(objectiveId, ScoreHistoryTake);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objectives: score history load failed for {ObjectiveId}", objectiveId);
            return Array.Empty<int>();
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
            _logger.LogDebug(ex, "Objectives: champion gate load failed for {ObjectiveId}", objectiveId);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Mirror of ObjectiveDisplayItem.PhasesSummary: compact multi-phase chip
    /// ("ALL PHASES" / "PRE + IN" / "POST"), falling back to the single-phase
    /// display label (upper-cased) when no practice bool is set.
    /// </summary>
    private static string BuildPhasesSummary(bool pre, bool inGame, bool post, string phaseLabel)
    {
        if (pre && inGame && post) return "ALL PHASES";
        var parts = new List<string>(3);
        if (pre) parts.Add("PRE");
        if (inGame) parts.Add("IN");
        if (post) parts.Add("POST");
        return parts.Count == 0 ? phaseLabel.ToUpperInvariant() : string.Join(" + ", parts);
    }

    /// <summary>Mirror of DashboardSnapshotBuilder.BuildObjectiveMetaText.</summary>
    private static string BuildObjectiveMetaText(
        bool isMini, int targetGameCount, int gameCount, string phaseLabel, string levelName, int score)
    {
        if (isMini)
        {
            var games = targetGameCount > 0
                ? $"{Math.Min(gameCount, targetGameCount)}/{targetGameCount} GAMES"
                : $"{gameCount} GAMES";
            return $"FOCUS  ·  {phaseLabel.ToUpperInvariant()}  ·  {games}";
        }
        return string.IsNullOrWhiteSpace(levelName)
            ? phaseLabel.ToUpperInvariant()
            : $"{levelName.ToUpperInvariant()}  ·  {phaseLabel.ToUpperInvariant()}  ·  {score} PTS";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spotted problems (mirror ObjectivesViewModel.SpottedProblemItem)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SpottedProblemDto>> BuildSpottedProblemsAsync()
    {
        try
        {
            // Lenient account-scope the spotted-problems suggestions to the
            // logged-in PUUID (own + legacy '' rows; foreign accounts excluded).
            // Empty puuid = no-op (all rows).
            var problems = await _gameAnalytics.GetRecentSpottedProblemsAsync(SpottedProblemsTake, _config.RiotPuuid);
            return problems.Select(p =>
            {
                var championDisplay = string.IsNullOrWhiteSpace(p.EnemyChampion)
                    ? p.ChampionName
                    : $"{p.ChampionName} vs {p.EnemyChampion}";
                return new SpottedProblemDto(
                    GameId: p.GameId,
                    ChampionName: p.ChampionName,
                    EnemyChampion: p.EnemyChampion,
                    DatePlayed: p.DatePlayed,
                    ProblemText: p.SpottedProblems,
                    Win: p.Win,
                    ResultText: p.Win ? "W" : "L",
                    ChampionDisplay: championDisplay,
                    ResultColorHex: p.Win ? WinHex : LossHex);
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Objectives: recent spotted problems load failed");
            return Array.Empty<SpottedProblemDto>();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Create-objective form descriptor (DEFERRED — read-only phase)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The create/edit objective form is a MUTATION and is deferred in the
    /// read-only sidecar phase. The frontend renders the form chrome but keys off
    /// <c>enabled: false</c> to disable submission. TODO: enable once a
    /// write-capable sidecar exposes CreateWithPhasesAndTargetAsync et al.
    /// </summary>
    private static CreateFormDto BuildCreateForm() => new(
        Enabled: false,
        Note: "Creating and editing objectives is coming soon.",
        TodoNote: "DEFERRED: mutations (create/edit/complete/delete/set-priority) are not wired in the read-only sidecar phase.");

    // ─────────────────────────────────────────────────────────────────────────
    // Objective level palette (mirror DashboardSnapshotBuilder; TODO: extract to Core)
    // ─────────────────────────────────────────────────────────────────────────

    private static string ObjectiveLevelHex(int levelIndex) => levelIndex switch
    {
        0 => "#7B8494",   // Exploring: Slate
        1 => "#5EC4D4",   // Drilling: Cyan
        2 => "#D4A44E",   // Ingraining: Amber
        3 => "#E8C15E",   // Ready: Bright gold
        _ => "#8A80A8",   // Neutral
    };

    private static string ObjectiveLevelDimHex(int levelIndex) => levelIndex switch
    {
        0 => "#10121A",
        1 => "#0E1A1E",
        2 => "#1E1810",
        3 => "#221C0E",
        _ => "#13111E",
    };
}

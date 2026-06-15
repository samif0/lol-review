#nullable enable

using System.Globalization;
using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only champ-select / in-game intel snapshot served at
/// GET /api/pregame[?myChampion=&amp;enemy=&amp;role=&amp;participantMap=].
///
/// <para>
/// Reproduces the READ half of <c>PreGameDialogViewModel.LoadAsync</c> EXACTLY —
/// the rotating intel deck, the active/priority objectives + their pre-game custom
/// prompts (with draft answers prefilled), saved matchup notes, the intent
/// carry-over seeds (carry / priority objective / lowest adherence) with their
/// provenance lines, the latest if-then plan, and the mood/intention gates — minus
/// every WinUI/dispatcher/messenger concern. It emits the camelCase JSON contract
/// (see <see cref="PreGameDto"/>).
/// </para>
///
/// <para>
/// The LIVE champ-select fields (my champ / enemy / role / 10-player participant
/// map → live matchup + 2v2 pairing) arrive over the SSE channel
/// (GET /api/events, ChampSelectStarted/Updated). This builder seeds the matchup
/// card from whatever live context is known at load (the query params, or the
/// shared <see cref="LcuLiveState"/> the hosted GameMonitorService keeps current).
/// </para>
///
/// <para>
/// READ-ONLY: only repository read methods + PreGameIntelService.BuildAsync are
/// called. The DEFERRED writes (mood, intent, practiced toggles, prompt-answer
/// drafts) are POSTed separately and captured into session_log / draft tables at
/// game END by the hosted SidecarGameFlowCoordinator — exactly like ShellViewModel.
/// </para>
/// </summary>
public sealed class PreGameSnapshotBuilder
{
    // ── Mockup palette (mirror AccentGold/Teal/Purple/Blue brushes) ─────────────
    private const string GoldHex = "#f3c794";
    private const string TealHex = "#7fe0d4";
    private const string PurpleHex = "#9d8bff";
    private const string BlueHex = "#7cc7ff";

    private readonly IGameRepository _gameRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IMatchupNotesRepository _matchupNotesRepo;
    private readonly IPromptsRepository _promptsRepo;
    private readonly ITiltCheckRepository _tiltChecks;
    private readonly IConfigService _configService;
    private readonly PreGameIntelService _intelService;
    private readonly LcuLiveState _liveState;
    private readonly ILogger<PreGameSnapshotBuilder> _logger;

    public PreGameSnapshotBuilder(
        IGameRepository gameRepo,
        IObjectivesRepository objectivesRepo,
        ISessionLogRepository sessionLogRepo,
        IMatchupNotesRepository matchupNotesRepo,
        IPromptsRepository promptsRepo,
        ITiltCheckRepository tiltChecks,
        IConfigService configService,
        PreGameIntelService intelService,
        LcuLiveState liveState,
        ILogger<PreGameSnapshotBuilder> logger)
    {
        _gameRepo = gameRepo;
        _objectivesRepo = objectivesRepo;
        _sessionLogRepo = sessionLogRepo;
        _matchupNotesRepo = matchupNotesRepo;
        _promptsRepo = promptsRepo;
        _tiltChecks = tiltChecks;
        _configService = configService;
        _intelService = intelService;
        _liveState = liveState;
        _logger = logger;
    }

    public async Task<PreGameDto> BuildAsync(
        string? myChampion, string? enemyChampion, string? myPosition, string? participantMapJson,
        CancellationToken ct = default)
    {
        var now = DateTime.Now;

        // Resolve the champ-select context: explicit query params win, else fall
        // back to whatever the live GameMonitor flow currently knows (so a webview
        // reload mid-champ-select still seeds the matchup card before the first SSE).
        var myChamp = FirstNonEmpty(myChampion, _liveState.MyChampion);
        var enemy = FirstNonEmpty(enemyChampion, _liveState.EnemyChampion);
        var position = FirstNonEmpty(myPosition, _liveState.MyPosition);
        var mapJson = FirstNonEmpty(participantMapJson, _liveState.ParticipantMapJson);

        // ── Carry seed (last review focus_next) ─────────────────────────────────
        var carrySeed = "";
        var carryProvenance = "";
        try
        {
            var lastReview = await _gameRepo.GetLastReviewFocusAsync().ConfigureAwait(false);
            if (lastReview is not null && !string.IsNullOrWhiteSpace(lastReview.FocusNext))
            {
                carrySeed = lastReview.FocusNext.Trim();
                carryProvenance = BuildCarryProvenance(lastReview);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreGame: last review focus load failed");
        }
        var hasCarrySource = !string.IsNullOrWhiteSpace(carrySeed);

        // ── Active pre-game objectives (champion-gated) ─────────────────────────
        var relevant = await LoadRelevantObjectivesAsync(myChamp).ConfigureAwait(false);
        var priority = relevant.FirstOrDefault(o => o.IsPriority);
        var priorityId = priority?.Id ?? 0L;

        var objectiveSeed = "";
        var objectiveProvenance = "";
        var priorityTitle = "";
        var priorityCriteria = "";
        var hasActiveObjective = relevant.Count > 0;
        if (hasActiveObjective)
        {
            var obj = priority ?? relevant[0];
            priorityTitle = obj.Title;
            priorityCriteria = obj.CompletionCriteria;
            objectiveSeed = string.IsNullOrWhiteSpace(priorityCriteria)
                ? priorityTitle
                : $"{priorityTitle}: {priorityCriteria}";
            objectiveProvenance = $"FROM PRIORITY OBJECTIVE: {priorityTitle.ToUpperInvariant()}";
        }
        var hasObjectiveSource = !string.IsNullOrWhiteSpace(objectiveSeed);

        // ── Lowest-adherence seed (data-gated until ~July 2026) ─────────────────
        var adherenceSeed = "";
        var adherenceProvenance = "";
        try
        {
            var weakest = await _objectivesRepo.GetLowestCriteriaAdherenceAsync().ConfigureAwait(false);
            if (weakest is not null)
            {
                adherenceSeed = string.IsNullOrWhiteSpace(weakest.CompletionCriteria)
                    ? weakest.Title
                    : $"{weakest.Title}: {weakest.CompletionCriteria}";
                var pct = weakest.Evaluated > 0
                    ? (int)Math.Round(100.0 * weakest.Hits / weakest.Evaluated)
                    : 0;
                adherenceProvenance =
                    $"WEAKEST OBJECTIVE: {weakest.Title.ToUpperInvariant()} · {weakest.Hits}/{weakest.Evaluated} HIT ({pct}%)";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreGame: lowest-adherence seed skipped");
        }
        var hasAdherenceSource = !string.IsNullOrWhiteSpace(adherenceSeed);

        // ── Zero-tap default seed: carry, else priority objective, else blank ───
        var seedText = "";
        var seedProvenance = "";
        var selectedSource = "";
        if (hasCarrySource)
        {
            seedText = carrySeed; seedProvenance = carryProvenance; selectedSource = "carry";
        }
        else if (hasObjectiveSource)
        {
            seedText = objectiveSeed; seedProvenance = objectiveProvenance; selectedSource = "objective";
        }

        // ── Latest if-then plan (display-only, ≤14d) ────────────────────────────
        string? activePlan = null;
        try
        {
            var plan = await _tiltChecks.GetLatestPlanAsync().ConfigureAwait(false);
            activePlan = string.IsNullOrWhiteSpace(plan) ? null : plan;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreGame: active plan read skipped");
        }

        // ── First-game-of-day + session intention gate ──────────────────────────
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var isFirstGame = false;
        try
        {
            var todayEntries = await _sessionLogRepo.GetForDateAsync(today).ConfigureAwait(false);
            isFirstGame = todayEntries.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreGame: first-game check failed");
        }
        var showIntention = _configService.TiltFixEnabled && isFirstGame;
        var sessionIntentionText = "";
        if (showIntention)
        {
            try
            {
                var session = await _sessionLogRepo.GetSessionAsync(today).ConfigureAwait(false);
                if (session is not null && !string.IsNullOrWhiteSpace(session.Intention))
                {
                    sessionIntentionText = session.Intention;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PreGame: session intention load failed");
            }
        }

        // ── Matchup notes (my champ vs enemy) ───────────────────────────────────
        var matchupHistory = await BuildMatchupHistoryAsync(myChamp, enemy).ConfigureAwait(false);

        // ── Pre-game custom prompts + staged draft answers ──────────────────────
        var promptBlocks = await BuildPromptBlocksAsync(myChamp).ConfigureAwait(false);

        // ── Rotating intel deck ─────────────────────────────────────────────────
        var intelDeck = await BuildIntelDeckAsync(myChamp, enemy, position, mapJson, ct).ConfigureAwait(false);

        // ── Objectives mega-card rows (practiced toggles) ───────────────────────
        var objectiveRows = relevant
            .Select(o => new PreGameObjectiveRowDto(
                ObjectiveId: o.Id,
                Title: o.Title,
                Criteria: o.CompletionCriteria,
                IsPriority: o.Id == priorityId))
            .ToList();

        return new PreGameDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            MyChampion: myChamp,
            EnemyChampion: enemy,
            MyPosition: position,
            IntelDeck: intelDeck,
            Matchup: new PreGameMatchupDto(
                HasMatchupDetected: !string.IsNullOrEmpty(myChamp),
                MyChampion: myChamp,
                EnemyOrPlaceholder: string.IsNullOrEmpty(enemy) ? "…" : enemy,
                AccentHex: BlueHex),
            MatchupHistory: matchupHistory,
            Intent: new PreGameIntentDto(
                SeedText: seedText,
                Provenance: seedProvenance,
                SelectedSource: selectedSource,
                HasCarrySource: hasCarrySource,
                CarrySeed: carrySeed,
                CarryProvenance: carryProvenance,
                HasObjectiveSource: hasObjectiveSource,
                ObjectiveSeed: objectiveSeed,
                ObjectiveProvenance: objectiveProvenance,
                HasAdherenceSource: hasAdherenceSource,
                AdherenceSeed: adherenceSeed,
                AdherenceProvenance: adherenceProvenance,
                AccentHex: GoldHex),
            ActivePlan: activePlan,
            ShowMoodSelector: true,
            SessionIntention: new PreGameSessionIntentionDto(
                Show: showIntention,
                IsFirstGame: isFirstGame,
                Intention: sessionIntentionText,
                QuickOptions: QuickIntentionOptions,
                AccentHex: PurpleHex),
            Objectives: new PreGameObjectivesDto(
                HasActiveObjective: hasActiveObjective,
                PriorityTitle: priorityTitle,
                PriorityCriteria: priorityCriteria,
                HasObjectives: objectiveRows.Count > 0,
                Items: objectiveRows,
                AccentHex: GoldHex),
            PromptBlocks: promptBlocks,
            GoldHex: GoldHex,
            TealHex: TealHex,
            PurpleHex: PurpleHex,
            BlueHex: BlueHex);
    }

    // ── The 3 if-then session-intention presets (QuickIntentionOptions). ───────
    private static readonly IReadOnlyList<string> QuickIntentionOptions = new[]
    {
        "When I die, I'll review why",
        "When tilted, I'll take 3 breaths",
        "When behind, I'll focus on farm",
    };

    private async Task<List<ObjectiveSummary>> LoadRelevantObjectivesAsync(string myChamp)
    {
        List<ObjectiveSummary> relevant;
        try
        {
            var objectives = await _objectivesRepo.GetActiveAsync().ConfigureAwait(false);
            relevant = objectives
                .Where(o => ObjectivePhases.ShowsInPreGame(o.Phase))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreGame: active objectives load failed");
            return new List<ObjectiveSummary>();
        }

        // Champion-gate in memory (mirror PreGameDialogViewModel): objectives with
        // no champion rows always pass; otherwise an OrdinalIgnoreCase match.
        if (!string.IsNullOrWhiteSpace(myChamp))
        {
            var filtered = new List<ObjectiveSummary>();
            foreach (var o in relevant)
            {
                try
                {
                    var champs = await _objectivesRepo.GetChampionsForObjectiveAsync(o.Id).ConfigureAwait(false);
                    if (champs.Count == 0
                        || champs.Any(c => string.Equals(c, myChamp, StringComparison.OrdinalIgnoreCase)))
                    {
                        filtered.Add(o);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PreGame: champion gate for objective {Id} failed", o.Id);
                    filtered.Add(o); // fail open — show the objective
                }
            }
            relevant = filtered;
        }

        return relevant;
    }

    private async Task<PreGameMatchupHistoryDto> BuildMatchupHistoryAsync(string myChamp, string enemy)
    {
        if (string.IsNullOrEmpty(myChamp) || string.IsNullOrEmpty(enemy))
        {
            return new PreGameMatchupHistoryDto(false, "", Array.Empty<PreGameMatchupNoteDto>(), BlueHex);
        }

        try
        {
            var notes = await _matchupNotesRepo.GetForMatchupAsync(myChamp, enemy).ConfigureAwait(false);
            if (notes.Count == 0)
            {
                return new PreGameMatchupHistoryDto(false, "", Array.Empty<PreGameMatchupNoteDto>(), BlueHex);
            }

            var items = notes.Select(note => new PreGameMatchupNoteDto(
                Note: note.Note,
                DateText: note.CreatedAt.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(note.CreatedAt.Value).LocalDateTime.ToString("MMM d", CultureInfo.InvariantCulture)
                    : "",
                WasHelpful: note.Helpful == 1,
                HasHelpfulRating: note.Helpful.HasValue)).ToList();

            return new PreGameMatchupHistoryDto(
                Has: true,
                HeaderText: $"YOUR NOTES vs {enemy.ToUpperInvariant()}",
                Items: items,
                AccentHex: BlueHex);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreGame: matchup history load failed");
            return new PreGameMatchupHistoryDto(false, "", Array.Empty<PreGameMatchupNoteDto>(), BlueHex);
        }
    }

    private async Task<List<PreGamePromptBlockDto>> BuildPromptBlocksAsync(string myChamp)
    {
        var blocks = new List<PreGamePromptBlockDto>();
        try
        {
            // Champion-gate prompts by the locked champion (null = show everything).
            var champGate = string.IsNullOrWhiteSpace(myChamp) ? null : myChamp;
            var active = await _promptsRepo.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame, champGate).ConfigureAwait(false);

            // Prefill draft answers staged under the current live session key (so an
            // answer typed in champ select survives a webview reload — same contract
            // as PreGameDialogViewModel's GetDraftAnswersAsync prefill).
            var draftTexts = new Dictionary<long, string>();
            var sessionKey = _liveState.SessionKey;
            if (!string.IsNullOrEmpty(sessionKey))
            {
                try
                {
                    foreach (var (promptId, answer) in await _promptsRepo.GetDraftAnswersAsync(sessionKey).ConfigureAwait(false))
                    {
                        draftTexts[promptId] = answer;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PreGame: draft answers load failed");
                }
            }

            // Group by objective (repo already sorts priority-first).
            var byObjective = new List<(long ObjectiveId, string Title, bool IsPriority, List<PreGamePromptDto> Prompts)>();
            foreach (var p in active)
            {
                if (byObjective.Count == 0 || byObjective[^1].ObjectiveId != p.ObjectiveId)
                {
                    byObjective.Add((p.ObjectiveId, p.ObjectiveTitle, p.IsPriority, new List<PreGamePromptDto>()));
                }
                byObjective[^1].Prompts.Add(new PreGamePromptDto(
                    PromptId: p.PromptId,
                    Label: p.Label,
                    AnswerText: draftTexts.TryGetValue(p.PromptId, out var draft) ? draft : ""));
            }

            foreach (var b in byObjective)
            {
                blocks.Add(new PreGamePromptBlockDto(
                    ObjectiveId: b.ObjectiveId,
                    ObjectiveTitle: b.Title,
                    IsPriority: b.IsPriority,
                    Eyebrow: b.IsPriority ? "PRIORITY" : "ACTIVE",
                    AccentHex: b.IsPriority ? GoldHex : TealHex,
                    Prompts: b.Prompts));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreGame: prompt blocks load failed");
        }
        return blocks;
    }

    private async Task<List<IntelCardDto>> BuildIntelDeckAsync(
        string myChamp, string enemy, string position, string mapJson, CancellationToken ct)
    {
        try
        {
            var cards = await _intelService.BuildAsync(myChamp, enemy, position, mapJson, ct).ConfigureAwait(false);
            return cards.Select(c => new IntelCardDto(c.Eyebrow, c.Headline, c.Body)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreGame: intel deck build failed");
            return new List<IntelCardDto>();
        }
    }

    // ── Provenance helpers (verbatim from PreGameDialogViewModel) ──────────────

    private static string BuildCarryProvenance(ReviewFocus lastReview)
    {
        var champPart = string.IsNullOrWhiteSpace(lastReview.ChampionName)
            ? ""
            : $", {lastReview.ChampionName.ToUpperInvariant()} ({(lastReview.Win ? "W" : "L")})";
        var age = FormatAge(lastReview.Timestamp);
        return string.IsNullOrEmpty(age)
            ? $"FROM YOUR LAST REVIEW{champPart}"
            : $"FROM YOUR LAST REVIEW{champPart} · {age}";
    }

    private static string FormatAge(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        var local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return $"TODAY {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"YESTERDAY {local:HH:mm}";
        return local.ToString("MMM d", CultureInfo.InvariantCulture).ToUpperInvariant();
    }

    private static string FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a!.Trim() : (b ?? "");
}

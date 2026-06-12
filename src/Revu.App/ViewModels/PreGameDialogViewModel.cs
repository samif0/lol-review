#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Services;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace Revu.App.ViewModels;

/// <summary>Parameter passed to the pre-game page carrying champion select info.</summary>
public sealed record PreGameChampInfo(string MyChampion, string EnemyLaner);

public sealed class FocusObjectiveItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool IsPriority { get; init; }
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
}

/// <summary>A previous matchup note shown in the pre-game panel.</summary>
public sealed class PreGameMatchupItem
{
    public string Note { get; init; } = "";
    public string DateText { get; init; } = "";
    public bool WasHelpful { get; init; }
    public bool HasHelpfulRating { get; init; }
}

/// <summary>
/// v2.15.0: one pre-game custom prompt + its current answer text. Grouped
/// into <see cref="PreGameObjectivePromptBlock"/> for rendering per-objective.
/// </summary>
public sealed partial class PreGamePromptAnswer : ObservableObject
{
    public long PromptId { get; init; }
    public string Label { get; init; } = "";

    // Upstream SaveDraftAnswerAsync fires when text changes; VM subscribes to
    // PropertyChanged on AnswerText and debounces the save.
    [ObservableProperty]
    private string _answerText = "";
}

/// <summary>v2.15.0: an objective + its pre-game prompts, grouped for the champ-select UI.</summary>
public sealed class PreGameObjectivePromptBlock
{
    public long ObjectiveId { get; init; }
    public string ObjectiveTitle { get; init; } = "";
    public bool IsPriority { get; init; }
    public ObservableCollection<PreGamePromptAnswer> Prompts { get; } = new();
    public string EyebrowText => IsPriority ? "PRIORITY" : "ACTIVE";
    public Microsoft.UI.Xaml.Media.SolidColorBrush AccentBrush =>
        IsPriority
            ? (Microsoft.UI.Xaml.Media.SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["AccentGoldBrush"]
            : (Microsoft.UI.Xaml.Media.SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["AccentTealBrush"];
}

/// <summary>ViewModel for the pre-game focus dialog shown during champion select.</summary>
public partial class PreGameDialogViewModel : ObservableObject, IRecipient<ChampSelectUpdatedMessage>
{
    private readonly IGameRepository _gameRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IMatchupNotesRepository _matchupNotesRepo;
    private readonly IPromptsRepository _promptsRepo;
    private readonly ITiltCheckRepository _tiltChecks;
    private readonly IConfigService _configService;
    private readonly IMessenger _messenger;
    private readonly PreGameIntelService _intelService;
    private readonly ILogger<PreGameDialogViewModel> _logger;

    // v2.15.0: stable session key for the current champ-select. Used to stage
    // prompt answers in pre_game_draft_prompts before the game row exists,
    // then promoted to prompt_answers at post-game via ShellViewModel.
    // Static so ShellViewModel can read the current key without extra DI wiring.
    internal static string? LastSessionKey { get; private set; }

    internal static void ResetSessionKey() => LastSessionKey = null;

    private string _sessionKey = "";

    // ── Observable Properties ───────────────────────────────────────

    [ObservableProperty]
    private string _lastFocus = "";

    [ObservableProperty]
    private bool _hasLastFocus;

    [ObservableProperty]
    private string _lastMistakes = "";

    [ObservableProperty]
    private bool _hasLastMistakes;

    [ObservableProperty]
    private string _activeObjectiveTitle = "";

    [ObservableProperty]
    private string _activeObjectiveCriteria = "";

    [ObservableProperty]
    private bool _hasActiveObjective;

    [ObservableProperty]
    private int _selectedMood;

    [ObservableProperty]
    private string _focusText = "";

    // ── v2.18 intent carry-over card (digest 2026-06-11-2 P2) ──────────
    // FocusText doubles as the card's editable text; these track where it
    // came from so the save can distinguish a zero-tap carry from intent.

    /// <summary>Provenance line above the intent box, e.g.
    /// "FROM YOUR LAST REVIEW — KAI'SA (L) · YESTERDAY 23:12".</summary>
    [ObservableProperty]
    private string _intentProvenance = "";

    public bool HasIntentProvenance => !string.IsNullOrWhiteSpace(IntentProvenance);

    partial void OnIntentProvenanceChanged(string value) => OnPropertyChanged(nameof(HasIntentProvenance));

    /// <summary>True when the user opted out — nothing is written this game.</summary>
    [ObservableProperty]
    private bool _isIntentCleared;

    [ObservableProperty]
    private bool _hasCarrySource;

    [ObservableProperty]
    private bool _hasObjectiveSource;

    /// <summary>Lowest-adherence chip: hidden until enough criteria_met data
    /// exists (repository data gate — first possible read ≈ July 2026).</summary>
    [ObservableProperty]
    private bool _hasAdherenceSource;

    [ObservableProperty]
    private bool _isCarrySourceSelected;

    [ObservableProperty]
    private bool _isObjectiveSourceSelected;

    [ObservableProperty]
    private bool _isAdherenceSourceSelected;

    // 'carry' | 'objective' | 'edited' | '' — the DB-facing source value.
    private string _intentSource = "";
    private bool _seedingIntent;
    private string _carrySeedText = "";
    private string _carryProvenance = "";
    private string _objectiveSeedText = "";
    private string _objectiveProvenance = "";
    private string _adherenceSeedText = "";
    private string _adherenceProvenance = "";

    /// <summary>v2.18 (digest 2026-06-12 P3b): latest Tilt Fix if-then plan
    /// (≤14 days old), shown read-only on the intent card. A plan only works
    /// if it's active when its cue occurs — until now saved plans were
    /// write-only. Never scored against behavior.</summary>
    [ObservableProperty]
    private string _activePlanText = "";

    public bool HasActivePlan => !string.IsNullOrWhiteSpace(ActivePlanText);

    partial void OnActivePlanTextChanged(string value) => OnPropertyChanged(nameof(HasActivePlan));

    [ObservableProperty]
    private string _sessionIntention = "";

    [ObservableProperty]
    private bool _isFirstGame;

    [ObservableProperty]
    private bool _showIntention;

    [ObservableProperty]
    private bool _showMoodSelector;

    [ObservableProperty]
    private bool _hasObjectiveFocusOptions;

    // Mood button highlight state
    [ObservableProperty]
    private bool _isTiltedSelected;

    [ObservableProperty]
    private bool _isOffSelected;

    [ObservableProperty]
    private bool _isNeutralSelected;

    [ObservableProperty]
    private bool _isGoodSelected;

    [ObservableProperty]
    private bool _isLockedInSelected;

    public static IReadOnlyList<string> QuickFocusOptions { get; } = new[]
    {
        "CS better early",
        "Track enemy JG",
        "Don't die before 6",
        "Play for teamfights",
        "Ward more",
        "Roam after push"
    };

    public static IReadOnlyList<string> QuickIntentionOptions { get; } = new[]
    {
        "When I die, I'll review why",
        "When tilted, I'll take 3 breaths",
        "When behind, I'll focus on farm"
    };

    public ObservableCollection<FocusObjectiveItem> ObjectiveFocusOptions { get; } = new();
    public ObservableCollection<ObjectiveAssessment> PreGameObjectives { get; } = new();
    public ObservableCollection<PreGameMatchupItem> MatchupHistory { get; } = new();

    /// <summary>v2.16.1: rotating intel deck shown at the top of the INTEL
    /// section. Mixes priority objective + last game + matchup notes + last
    /// pre-game answers + enemy ability cooldowns. IntelRotatorControl
    /// crossfades through these every few seconds.</summary>
    public ObservableCollection<IntelCard> IntelDeck { get; } = new();

    [ObservableProperty]
    private bool _hasIntelDeck;

    // v2.15.0: one block per active pre-phase objective, containing its custom prompts.
    public ObservableCollection<PreGameObjectivePromptBlock> PreGamePromptBlocks { get; } = new();

    [ObservableProperty]
    private bool _hasMatchupHistory;

    [ObservableProperty]
    private bool _hasPreGameObjectives;

    [ObservableProperty]
    private bool _hasPreGamePromptBlocks;

    [ObservableProperty]
    private string _matchupHeaderText = "";

    // ── Detected matchup (even when no notes exist) ──────────────────

    /// <summary>Local player's locked-in champion (empty until lock or if only hovered).</summary>
    [ObservableProperty]
    private string _myChampionName = "";

    /// <summary>Enemy laner's locked-in champion (empty if enemy not locked, or no lane assignment e.g. ARAM).</summary>
    [ObservableProperty]
    private string _enemyChampionName = "";

    /// <summary>v2.16.4: full role→champion JSON map for both teams keyed
    /// from user perspective. Drives the 2v2 matchup pairing string.</summary>
    [ObservableProperty]
    private string _liveParticipantMapJson = "";

    /// <summary>v2.16.4: 2v2 matchup pairing string when role + map allow it,
    /// otherwise empty so the lane-only "Champ vs Enemy" card stays the
    /// fallback. ADC = "Kai'Sa+Nautilus vs Tristana+Renata" etc.</summary>
    public string PairingHeadline => BuildPairingHeadline();

    public bool HasPairingHeadline => !string.IsNullOrEmpty(PairingHeadline);

    private string BuildPairingHeadline()
    {
        if (string.IsNullOrWhiteSpace(LiveParticipantMapJson)) return "";
        var role = (MyPositionInternal ?? "").ToLowerInvariant();
        if (string.IsNullOrEmpty(role)) return "";

        Dictionary<string, string>? map = null;
        try { map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(LiveParticipantMapJson); }
        catch { return ""; }
        if (map is null || map.Count == 0) return "";

        return role switch
        {
            "adc" or "bottom" or "bot" =>
                Pair(map, "ownBot", "ownSupp", "enemyBot", "enemySupp"),
            "support" or "supp" or "utility" =>
                Pair(map, "ownSupp", "ownBot", "enemySupp", "enemyBot"),
            "mid" or "middle" =>
                Pair(map, "ownMid", "ownJg", "enemyMid", "enemyJg"),
            "jungle" or "jg" =>
                Pair(map, "ownJg", "ownMid", "enemyJg", "enemyMid"),
            _ => "",
        };
    }

    private static string Pair(IReadOnlyDictionary<string, string> map,
        string ownPrimary, string ownPartner, string enemyPrimary, string enemyPartner)
    {
        if (!map.TryGetValue(ownPrimary, out var op) || string.IsNullOrEmpty(op)) return "";
        if (!map.TryGetValue(enemyPrimary, out var ep) || string.IsNullOrEmpty(ep)) return "";
        var ownPart = map.TryGetValue(ownPartner, out var v1) ? v1 : "";
        var enemyPart = map.TryGetValue(enemyPartner, out var v2) ? v2 : "";
        var ownStr = string.IsNullOrEmpty(ownPart) ? op : $"{op}+{ownPart}";
        var enemyStr = string.IsNullOrEmpty(enemyPart) ? ep : $"{ep}+{enemyPart}";
        return $"{ownStr} vs {enemyStr}";
    }

    /// <summary>v2.16.4: cached uppercase role for pairing derivation.
    /// Set from ChampSelect{Started,Updated}Message handlers.</summary>
    [ObservableProperty]
    private string _myPositionInternal = "";

    partial void OnLiveParticipantMapJsonChanged(string value) { OnPropertyChanged(nameof(PairingHeadline)); OnPropertyChanged(nameof(HasPairingHeadline)); }
    partial void OnMyPositionInternalChanged(string value) { OnPropertyChanged(nameof(PairingHeadline)); OnPropertyChanged(nameof(HasPairingHeadline)); }

    /// <summary>
    /// True when both champions are known — means we can show a visual "VS." card
    /// even if the user has zero saved notes for this matchup yet.
    /// </summary>
    [ObservableProperty]
    private bool _hasMatchupDetected;

    /// <summary>
    /// True when the INTEL section has nothing else to show yet — drives a
    /// waiting-state placeholder so the "— INTEL —" header doesn't sit
    /// orphaned above an empty region while champ select is still loading.
    /// </summary>
    public bool ShowIntelWaiting =>
        !HasMatchupDetected && !HasMatchupHistory && !HasLastFocus;

    partial void OnHasMatchupDetectedChanged(bool value) => OnPropertyChanged(nameof(ShowIntelWaiting));
    partial void OnHasMatchupHistoryChanged(bool value) => OnPropertyChanged(nameof(ShowIntelWaiting));
    partial void OnHasLastFocusChanged(bool value) => OnPropertyChanged(nameof(ShowIntelWaiting));

    /// <summary>Snapshot of practiced objective IDs from the last pre-game session, read by ShellViewModel on game end.</summary>
    internal static IReadOnlyList<long> LastPracticedObjectiveIds { get; set; } = [];

    // ── v2.18 (schema v6): pre-game snapshots consumed by ShellViewModel at
    // game end — the write hop the audit found severed (brief 2026-06-11-03).
    // Statics, not instance state: InGamePage gets a fresh VM instance and the
    // EOG handler runs in ShellViewModel, so instance state would be lost
    // (same reasoning as LastSessionKey / P-002).

    /// <summary>Mirror of SelectedMood for the EOG session_log write.</summary>
    internal static int LastPreGameMood { get; private set; }

    /// <summary>The intent text to persist at game end ("" = nothing).</summary>
    internal static string LastIntention { get; private set; } = "";

    /// <summary>'carry' | 'objective' | 'edited' | '' — provenance for
    /// session_log.intention_source (digest 2026-06-11-2 rider 2a).</summary>
    internal static string LastIntentionSource { get; private set; } = "";

    /// <summary>True when the user explicitly opted out via "don't carry".</summary>
    internal static bool LastIntentCleared { get; private set; }

    internal static void ResetPreGameSnapshots()
    {
        LastPreGameMood = 0;
        LastIntention = "";
        LastIntentionSource = "";
        LastIntentCleared = false;
    }

    // ── Constructor ─────────────────────────────────────────────────

    public PreGameDialogViewModel(
        IGameRepository gameRepo,
        IObjectivesRepository objectivesRepo,
        ISessionLogRepository sessionLogRepo,
        IMatchupNotesRepository matchupNotesRepo,
        IPromptsRepository promptsRepo,
        ITiltCheckRepository tiltChecks,
        IConfigService configService,
        IMessenger messenger,
        PreGameIntelService intelService,
        ILogger<PreGameDialogViewModel> logger)
    {
        _gameRepo = gameRepo;
        _objectivesRepo = objectivesRepo;
        _sessionLogRepo = sessionLogRepo;
        _matchupNotesRepo = matchupNotesRepo;
        _promptsRepo = promptsRepo;
        _tiltChecks = tiltChecks;
        _configService = configService;
        _messenger = messenger;
        _intelService = intelService;
        _logger = logger;
    }

    public void Attach() => _messenger.RegisterAll(this);

    public void Detach() => _messenger.UnregisterAll(this);

    public void Receive(ChampSelectUpdatedMessage message)
    {
        BackgroundTaskRunner.Run(() => Helpers.DispatcherHelper.RunOnUIThreadAsync(async () =>
        {
            try
            {
                MyPositionInternal = message.MyPosition ?? "";
                LiveParticipantMapJson = message.ParticipantMapJson ?? "";
                await LoadMatchupHistoryAsync(message.MyChampion, message.EnemyLaner);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload matchup history on champ-select update");
            }

            // v2.18 (F5): rebuild the intel/cooldown deck independently of the
            // matchup-history load above. The enemy champion often isn't locked
            // when champ select first opens, so the initial deck has no cooldowns;
            // this is the path that fills them in once the enemy locks. Kept
            // separate so a matchup-history failure can't also blank the deck.
            try
            {
                MyChampionName = message.MyChampion ?? MyChampionName;
                EnemyChampionName = message.EnemyLaner ?? EnemyChampionName;
                await RefreshIntelDeckAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh intel deck on champ-select update");
            }
        }), _logger, "champ-select matchup refresh");
    }

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync(PreGameChampInfo? champInfo)
    {
        try
        {
            // v2.18: same champ-select → game flow when a session key is live
            // (InGamePage shares this VM type and re-runs LoadAsync — P-002).
            // A fresh flow re-seeds; a continuing flow must preserve the
            // user's pre-game choices, which live in the static snapshots.
            var continuingFlow = !string.IsNullOrEmpty(LastSessionKey);
            if (!continuingFlow)
            {
                IsIntentCleared = false;
                _intentSource = "";
                _carrySeedText = ""; _carryProvenance = "";
                _objectiveSeedText = ""; _objectiveProvenance = "";
                _adherenceSeedText = ""; _adherenceProvenance = "";
                IsCarrySourceSelected = false;
                IsObjectiveSourceSelected = false;
                IsAdherenceSourceSelected = false;
            }

            // v2.18 (digest 2026-06-11-2 P2): mood picker un-gated from Tilt
            // Fix — the gate kept the instrument dark for users with
            // tilt_fix_mode off, which is why pre_game_mood had 0 rows ever.
            ShowMoodSelector = true;
            if (continuingFlow && LastPreGameMood > 0 && SelectedMood == 0)
            {
                SelectedMood = LastPreGameMood;
            }

            // Get last review focus
            var lastReview = await _gameRepo.GetLastReviewFocusAsync();
            if (lastReview != null)
            {
                LastFocus = lastReview.FocusNext;
                HasLastFocus = !string.IsNullOrWhiteSpace(lastReview.FocusNext);
                LastMistakes = lastReview.Mistakes;
                HasLastMistakes = !string.IsNullOrWhiteSpace(lastReview.Mistakes);

                if (HasLastFocus)
                {
                    _carrySeedText = lastReview.FocusNext.Trim();
                    _carryProvenance = BuildCarryProvenance(lastReview);
                }
            }
            HasCarrySource = !string.IsNullOrWhiteSpace(_carrySeedText);

            // Zero-tap default: seed from the last review's focus_next. The
            // blank-box variant of this feature already failed 0/47 in the
            // Python era (brief 2026-06-11-03) — do-nothing must equal
            // carry-forward. User edits are restored further down.
            if (HasCarrySource)
            {
                ApplySeed(_carrySeedText, _carryProvenance, "carry", "carry");
            }

            // Get active objective
            var objectives = await _objectivesRepo.GetActiveAsync();
            // v2.15.0: champion-gate the practice list too. We apply the filter
            // in-memory here (rather than adding a champion param to
            // GetActiveAsync) because the rest of this method is already doing
            // phase-based in-memory filtering.
            var champName = champInfo?.MyChampion ?? "";
            var relevantObjectives = objectives
                .Where(objective => ObjectivePhases.ShowsInPreGame(objective.Phase))
                .ToList();

            if (!string.IsNullOrWhiteSpace(champName))
            {
                var filtered = new List<ObjectiveSummary>();
                foreach (var o in relevantObjectives)
                {
                    var champs = await _objectivesRepo.GetChampionsForObjectiveAsync(o.Id);
                    if (champs.Count == 0
                        || champs.Any(c => string.Equals(c, champName, StringComparison.OrdinalIgnoreCase)))
                    {
                        filtered.Add(o);
                    }
                }
                relevantObjectives = filtered;
            }
            var priorityObjective = relevantObjectives.FirstOrDefault(objective => objective.IsPriority);
            var priorityObjectiveId = priorityObjective?.Id ?? 0L;
            ObjectiveFocusOptions.Clear();
            if (relevantObjectives.Count > 0)
            {
                foreach (var objective in relevantObjectives)
                {
                    ObjectiveFocusOptions.Add(new FocusObjectiveItem
                    {
                        Id = objective.Id,
                        Title = objective.Title,
                        Subtitle = string.IsNullOrWhiteSpace(objective.CompletionCriteria)
                            ? ObjectivePhases.ToDisplayLabel(objective.Phase)
                            : $"{ObjectivePhases.ToDisplayLabel(objective.Phase)} · {objective.CompletionCriteria}",
                        IsPriority = objective.Id == priorityObjectiveId
                    });
                }

                HasObjectiveFocusOptions = ObjectiveFocusOptions.Count > 0;

                var obj = priorityObjective ?? relevantObjectives[0];
                ActiveObjectiveTitle = obj.Title;
                ActiveObjectiveCriteria = obj.CompletionCriteria;
                HasActiveObjective = true;

                // Objective seed: criteria phrasing when it exists (executable
                // beats decorative), title otherwise.
                _objectiveSeedText = string.IsNullOrWhiteSpace(ActiveObjectiveCriteria)
                    ? ActiveObjectiveTitle
                    : $"{ActiveObjectiveTitle} — {ActiveObjectiveCriteria}";
                _objectiveProvenance = $"FROM PRIORITY OBJECTIVE — {ActiveObjectiveTitle.ToUpperInvariant()}";

                // Fallback seed when there is no review focus to carry.
                if (!HasCarrySource && !string.IsNullOrWhiteSpace(_objectiveSeedText))
                {
                    ApplySeed(_objectiveSeedText, _objectiveProvenance, "objective", "objective");
                }
            }
            else
            {
                HasObjectiveFocusOptions = false;
                HasActiveObjective = false;
                ActiveObjectiveTitle = "";
                ActiveObjectiveCriteria = "";
            }
            HasObjectiveSource = !string.IsNullOrWhiteSpace(_objectiveSeedText);

            // v2.18: third seed — lowest criteria adherence. The repository
            // data-gates this (null until ≥10 evaluated rows, ≥3 per
            // objective), so the chip stays hidden until ~July 2026 data.
            try
            {
                var weakest = await _objectivesRepo.GetLowestCriteriaAdherenceAsync();
                if (weakest is not null)
                {
                    _adherenceSeedText = string.IsNullOrWhiteSpace(weakest.CompletionCriteria)
                        ? weakest.Title
                        : $"{weakest.Title} — {weakest.CompletionCriteria}";
                    var pct = weakest.Evaluated > 0
                        ? (int)Math.Round(100.0 * weakest.Hits / weakest.Evaluated)
                        : 0;
                    _adherenceProvenance =
                        $"WEAKEST OBJECTIVE — {weakest.Title.ToUpperInvariant()} · {weakest.Hits}/{weakest.Evaluated} HIT ({pct}%)";
                }
                HasAdherenceSource = !string.IsNullOrWhiteSpace(_adherenceSeedText);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Lowest-adherence seed skipped");
                HasAdherenceSource = false;
            }

            // v2.18 (digest 2026-06-12 P3b): latest if-then plan from a Tilt
            // Fix run, display-only. Re-exposure at champ select is the
            // mechanism implementation intentions need; nothing is tracked.
            try
            {
                ActivePlanText = await _tiltChecks.GetLatestPlanAsync() ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Active if-then plan read skipped");
                ActivePlanText = "";
            }

            // Continuing flow: re-apply what the user already chose this flow
            // so the InGamePage re-run doesn't clobber it (mirrors the P-002
            // draft-restore fix for prompt answers).
            if (continuingFlow)
            {
                if (LastIntentCleared)
                {
                    IsIntentCleared = true;
                    IntentProvenance = "NOTHING CARRIED THIS GAME";
                }
                else if (LastIntentionSource == "edited")
                {
                    _seedingIntent = true;
                    FocusText = LastIntention;
                    _seedingIntent = false;
                    _intentSource = "edited";
                    IntentProvenance = "EDITED BY YOU — SAVES AS WRITTEN";
                    IsCarrySourceSelected = false;
                    IsObjectiveSourceSelected = false;
                    IsAdherenceSourceSelected = false;
                }
            }
            UpdateIntentSnapshot();

            // Populate pre-game objectives with practiced toggles
            PreGameObjectives.Clear();
            foreach (var objective in relevantObjectives)
            {
                var assessment = new ObjectiveAssessment
                {
                    ObjectiveId = objective.Id,
                    Title = objective.Title,
                    Criteria = objective.CompletionCriteria,
                    Phase = objective.Phase,
                    IsPriority = objective.Id == priorityObjectiveId
                };
                assessment.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ObjectiveAssessment.Practiced))
                        SnapshotPracticedIds();
                };
                PreGameObjectives.Add(assessment);
            }
            HasPreGameObjectives = PreGameObjectives.Count > 0;
            LastPracticedObjectiveIds = [];

            // Check if first game of the day
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var todayEntries = await _sessionLogRepo.GetForDateAsync(today);
            IsFirstGame = todayEntries.Count == 0;
            // ShowMoodSelector is un-gated now (v2.18); the session-intention
            // section keeps its original Tilt Fix gate — only mood was approved
            // for un-gating (digest 2026-06-11-2 P2).
            ShowIntention = _configService.TiltFixEnabled && IsFirstGame;

            // Load existing session intention
            if (ShowIntention)
            {
                var session = await _sessionLogRepo.GetSessionAsync(today);
                if (session != null && !string.IsNullOrWhiteSpace(session.Intention))
                {
                    SessionIntention = session.Intention;
                }
            }

            // Load matchup history if we have champion info
            await LoadMatchupHistoryAsync(champInfo?.MyChampion, champInfo?.EnemyLaner);

            // v2.15.0: session key scopes draft prompt answers to one
            // champ-select → game flow. Reuse the active key when one exists:
            // InGamePage shares this VM type and re-runs LoadAsync on navigate,
            // and minting a fresh key there orphaned the champ-select drafts so
            // post-game promotion found nothing. ShellViewModel resets the key
            // after promoting at game end, so non-null means "same flow".
            if (string.IsNullOrEmpty(LastSessionKey))
            {
                _sessionKey = Guid.NewGuid().ToString("N");
                LastSessionKey = _sessionKey;
            }
            else
            {
                _sessionKey = LastSessionKey;
            }
            await LoadPreGamePromptsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pre-game data");
        }
    }

    private async Task LoadPreGamePromptsAsync()
    {
        PreGamePromptBlocks.Clear();
        try
        {
            // v2.15.0: champion-gate prompts by the locked-in champion. NULL
            // champion name (hovering or not locked yet) disables the filter
            // so the user sees everything they might need.
            var champGate = string.IsNullOrWhiteSpace(MyChampionName) ? null : MyChampionName;
            var active = await _promptsRepo.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame, champGate);

            // Drafts already staged under this session key prefill the boxes,
            // so text typed in champ select survives re-navigation and shows
            // on InGamePage mid-game instead of silently vanishing.
            var draftTexts = new Dictionary<long, string>();
            foreach (var (draftPromptId, draftAnswer) in await _promptsRepo.GetDraftAnswersAsync(_sessionKey))
            {
                draftTexts[draftPromptId] = draftAnswer;
            }

            // Group by objective; sort is already applied by the repo query.
            PreGameObjectivePromptBlock? current = null;
            foreach (var p in active)
            {
                if (current is null || current.ObjectiveId != p.ObjectiveId)
                {
                    current = new PreGameObjectivePromptBlock
                    {
                        ObjectiveId = p.ObjectiveId,
                        ObjectiveTitle = p.ObjectiveTitle,
                        IsPriority = p.IsPriority,
                    };
                    PreGamePromptBlocks.Add(current);
                }

                var answer = new PreGamePromptAnswer
                {
                    PromptId = p.PromptId,
                    Label = p.Label,
                };
                // Prefill before wiring the save handler so restoring a draft
                // doesn't immediately re-write it.
                if (draftTexts.TryGetValue(p.PromptId, out var existingDraft))
                {
                    answer.AnswerText = existingDraft;
                }
                // Debounce-ish save: write to drafts on every text change. Cheap —
                // it's a single upsert — and protects against losing the answer
                // if the app crashes before game end.
                answer.PropertyChanged += async (_, ev) =>
                {
                    if (ev.PropertyName != nameof(PreGamePromptAnswer.AnswerText)) return;
                    try
                    {
                        await _promptsRepo.SaveDraftAnswerAsync(_sessionKey, answer.PromptId, answer.AnswerText);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to persist pre-game prompt draft");
                    }
                };
                current.Prompts.Add(answer);
            }

            HasPreGamePromptBlocks = PreGamePromptBlocks.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pre-game prompts");
            HasPreGamePromptBlocks = false;
        }
    }

    private async Task LoadMatchupHistoryAsync(string? myChampion, string? enemyLaner)
    {
        MatchupHistory.Clear();
        HasMatchupHistory = false;
        MatchupHeaderText = "";

        MyChampionName = myChampion ?? "";
        EnemyChampionName = enemyLaner ?? "";
        // Show the matchup card as soon as MY champ is locked, even if the enemy hasn't
        // locked yet. The card's enemy column will display the waiting-state below.
        HasMatchupDetected = !string.IsNullOrEmpty(MyChampionName);

        // v2.16.1: refresh the rotating intel deck on EVERY champ-select tick,
        // not just when both champs are locked. Priority-objective + last-game
        // cards don't depend on enemy, so the rotator should pop immediately.
        BackgroundTaskRunner.Run(RefreshIntelDeckAsync, _logger, "pre-game intel refresh");

        if (string.IsNullOrEmpty(myChampion) || string.IsNullOrEmpty(enemyLaner))
        {
            return;
        }

        var notes = await _matchupNotesRepo.GetForMatchupAsync(myChampion, enemyLaner);
        if (notes.Count == 0)
        {
            return;
        }

        MatchupHeaderText = $"YOUR NOTES vs {enemyLaner.ToUpperInvariant()}";
        foreach (var note in notes)
        {
            var dateText = note.CreatedAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(note.CreatedAt.Value).LocalDateTime.ToString("MMM d")
                : "";
            MatchupHistory.Add(new PreGameMatchupItem
            {
                Note = note.Note,
                DateText = dateText,
                WasHelpful = note.Helpful == 1,
                HasHelpfulRating = note.Helpful.HasValue
            });
        }
        HasMatchupHistory = MatchupHistory.Count > 0;
    }

    private async Task RefreshIntelDeckAsync()
    {
        try
        {
            var cards = await _intelService.BuildAsync(
                MyChampionName, EnemyChampionName, MyPositionInternal, LiveParticipantMapJson);
            await Helpers.DispatcherHelper.RunOnUIThreadAsync(() =>
            {
                IntelDeck.Clear();
                foreach (var c in cards) IntelDeck.Add(c);
                HasIntelDeck = IntelDeck.Count > 0;
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Intel deck refresh failed");
        }
    }

    [RelayCommand]
    private void SelectMood(int mood)
    {
        SelectedMood = mood;
    }

    // Runs for both entry points — the SelectMood command and the
    // MoodSelector's two-way binding — so the highlight flags and the EOG
    // snapshot can't drift apart. The snapshot is the previously-severed
    // wire: nothing read SelectedMood after the dialog closed (brief
    // 2026-06-11-03).
    partial void OnSelectedMoodChanged(int value)
    {
        IsTiltedSelected = value == 1;
        IsOffSelected = value == 2;
        IsNeutralSelected = value == 3;
        IsGoodSelected = value == 4;
        IsLockedInSelected = value == 5;
        LastPreGameMood = value;
    }

    [RelayCommand]
    private void SetQuickFocus(string text)
    {
        FocusText = text;
    }

    [RelayCommand]
    private void SetObjectiveFocus(string title)
    {
        FocusText = title;
    }

    [RelayCommand]
    private void SetQuickIntention(string text)
    {
        SessionIntention = text;
    }

    private void SnapshotPracticedIds()
    {
        LastPracticedObjectiveIds = PreGameObjectives
            .Where(static o => o.Practiced)
            .Select(static o => o.ObjectiveId)
            .ToList();
    }

    // ── v2.18 intent carry-over internals ───────────────────────────────

    /// <summary>User typed in the intent box: provenance flips to "edited"
    /// and the chips deselect. Programmatic seeds suppress this via
    /// <see cref="_seedingIntent"/>.</summary>
    partial void OnFocusTextChanged(string value)
    {
        if (_seedingIntent) return;
        _intentSource = "edited";
        IntentProvenance = "EDITED BY YOU — SAVES AS WRITTEN";
        IsCarrySourceSelected = false;
        IsObjectiveSourceSelected = false;
        IsAdherenceSourceSelected = false;
        UpdateIntentSnapshot();
    }

    [RelayCommand]
    private void UseCarrySource()
    {
        if (!HasCarrySource) return;
        ApplySeed(_carrySeedText, _carryProvenance, "carry", "carry");
    }

    [RelayCommand]
    private void UseObjectiveSource()
    {
        if (!HasObjectiveSource) return;
        ApplySeed(_objectiveSeedText, _objectiveProvenance, "objective", "objective");
    }

    [RelayCommand]
    private void UseAdherenceSource()
    {
        if (!HasAdherenceSource) return;
        // DB source stays 'objective' — the adherence chip is just a different
        // way of picking an objective seed (rider 2a value set is closed).
        ApplySeed(_adherenceSeedText, _adherenceProvenance, "objective", "adherence");
    }

    /// <summary>v2.18 (digest 2026-06-12 P3a): optional reshape of the current
    /// intention into if-then form. The app contributes only the skeleton —
    /// the user's own text becomes the action half when present. Goes through
    /// OnFocusTextChanged, so intention_source becomes 'edited'.</summary>
    [RelayCommand]
    private void ApplyIfThenScaffold()
    {
        var current = FocusText.Trim();
        if (current.StartsWith("If ", StringComparison.OrdinalIgnoreCase))
            return; // already in if-then form
        FocusText = string.IsNullOrEmpty(current)
            ? "If [moment], then I will [action]"
            : $"If [moment], then I will {current}";
    }

    /// <summary>"✕ don't carry" / "restore" toggle. Cleared = nothing is
    /// written this game — the zero-tap default needs an explicit exit or it
    /// would manufacture false intention rows.</summary>
    [RelayCommand]
    private void ToggleIntentCarry()
    {
        if (IsIntentCleared)
        {
            IsIntentCleared = false;
            if (HasCarrySource)
            {
                ApplySeed(_carrySeedText, _carryProvenance, "carry", "carry");
            }
            else if (HasObjectiveSource)
            {
                ApplySeed(_objectiveSeedText, _objectiveProvenance, "objective", "objective");
            }
            else
            {
                _seedingIntent = true;
                FocusText = "";
                _seedingIntent = false;
                _intentSource = "";
                IntentProvenance = "";
                UpdateIntentSnapshot();
            }
        }
        else
        {
            IsIntentCleared = true;
            IntentProvenance = "NOTHING CARRIED THIS GAME";
            UpdateIntentSnapshot();
        }
    }

    private void ApplySeed(string text, string provenance, string dbSource, string chip)
    {
        _seedingIntent = true;
        FocusText = text;
        _seedingIntent = false;
        _intentSource = dbSource;
        IntentProvenance = provenance;
        IsIntentCleared = false;
        // All-false first so re-clicking the already-selected chip still
        // raises a change — the ToggleButton self-toggles a local value that
        // only a fresh notification overwrites.
        IsCarrySourceSelected = false;
        IsObjectiveSourceSelected = false;
        IsAdherenceSourceSelected = false;
        switch (chip)
        {
            case "carry": IsCarrySourceSelected = true; break;
            case "objective": IsObjectiveSourceSelected = true; break;
            case "adherence": IsAdherenceSourceSelected = true; break;
        }
        UpdateIntentSnapshot();
    }

    /// <summary>Keep the static EOG snapshot in lock-step with the card.</summary>
    private void UpdateIntentSnapshot()
    {
        LastIntentCleared = IsIntentCleared;
        var text = IsIntentCleared ? "" : FocusText.Trim();
        LastIntention = text;
        LastIntentionSource = string.IsNullOrWhiteSpace(text) ? "" : _intentSource;
    }

    private static string BuildCarryProvenance(ReviewFocus lastReview)
    {
        var champPart = string.IsNullOrWhiteSpace(lastReview.ChampionName)
            ? ""
            : $" — {lastReview.ChampionName.ToUpperInvariant()} ({(lastReview.Win ? "W" : "L")})";
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
        return local.ToString("MMM d").ToUpperInvariant();
    }
}

#nullable enable

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.ViewModels;

/// <summary>ViewModel for the inline game review page.</summary>
public partial class ReviewViewModel : ObservableObject,
    IRecipient<Revu.Core.Lcu.BookmarkChangedMessage>
{
    private const int MaxAutoTimelineEvidence = 8;
    private const int MaxEvidenceInboxItems = 12;
    private const int EvidenceJumpPreRollSeconds = 5;

    // P-006: deaths get a longer run-up than evidence clips — the cause
    // (greed, missing vision, wave state) is visible in the approach.
    private const int DeathJumpPreRollSeconds = 10;

    private readonly IReviewWorkflowService _reviewWorkflowService;
    private readonly INavigationService _navigationService;
    private readonly IPromptsRepository _promptsRepository;
    private readonly IObjectivesRepository _objectivesRepository;
    private readonly IVodRepository _vodRepository;
    private readonly IGameEventsRepository _eventsRepository;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly IReviewExportService _reviewExportService;
    private readonly ISessionLogRepository _sessionLogRepository;
    private readonly IDeathClassificationsRepository _deathClassificationsRepository;
    private readonly IMessenger _messenger;
    private readonly ILogger<ReviewViewModel> _logger;

    [ObservableProperty] private long _gameId;
    [ObservableProperty] private string _championName = "";
    [ObservableProperty] private bool _win;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _resultColorHex = AppSemanticPalette.PrimaryTextHex;
    [ObservableProperty] private string _kdaText = "";
    [ObservableProperty] private string _kdaRatioText = "";
    [ObservableProperty] private string _gameModeText = "";
    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private string _headerText = "Review";
    [ObservableProperty] private string _enemyLaner = "";
    [ObservableProperty] private bool _hasEnemyLaner;
    [ObservableProperty] private string _damageText = "";
    [ObservableProperty] private string _csText = "";
    [ObservableProperty] private string _csPerMinText = "";
    [ObservableProperty] private string _visionText = "";
    [ObservableProperty] private string _goldText = "";
    [ObservableProperty] private string _killParticipationText = "";
    [ObservableProperty] private string _damageTakenText = "";
    [ObservableProperty] private string _wardsPlacedText = "";
    [ObservableProperty] private bool _hasVod;
    [ObservableProperty] private int _bookmarkCount;
    [ObservableProperty] private int _mentalRating = 5;
    [ObservableProperty] private string _mentalRatingColorHex = AppSemanticPalette.AccentBlueHex;
    [ObservableProperty] private string _wentWell = "";
    [ObservableProperty] private string _mistakes = "";
    [ObservableProperty] private string _focusNext = "";
    [ObservableProperty] private string _reviewNotes = "";
    [ObservableProperty] private string _improvementNote = "";
    [ObservableProperty] private string _attribution = "";
    [ObservableProperty] private string _mentalHandled = "";
    [ObservableProperty] private string _spottedProblems = "";
    [ObservableProperty] private string _outsideControl = "";
    [ObservableProperty] private string _withinControl = "";

    /// <summary>v2.18 (digest 2026-06-11-2 P3a): reappraisal section expander.
    /// Collapsed by default so the two extra free-text fields stay opt-in;
    /// auto-expands on load when saved text exists.</summary>
    [ObservableProperty] private bool _isReappraisalExpanded;
    [ObservableProperty] private string _personalContribution = "";
    [ObservableProperty] private string _matchupNote = "";
    [ObservableProperty] private bool _requireReviewNotes;
    [ObservableProperty] private string _saveBehaviorText = "";
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private bool _hasValidationMessage;
    [ObservableProperty] private bool _showMentalReflection;
    [ObservableProperty] private bool _isLoading;
    private CancellationTokenSource? _vodRetryCts;
    [ObservableProperty] private bool _hasObjectives;
    [ObservableProperty] private bool _hasMatchupHistory;
    [ObservableProperty] private string _priorityObjectiveTitle = "";
    [ObservableProperty] private string _priorityObjectiveCriteria = "";
    [ObservableProperty] private bool _hasPriorityObjective;
    [ObservableProperty] private bool _isExportingReview;
    [ObservableProperty] private string _exportStatusText = "";
    [ObservableProperty] private bool _hasEvidenceItems;
    [ObservableProperty] private bool _isEvidenceLoading;
    // v2.15.0: show the collapsible "legacy fields" card only when this game
    // was reviewed on an earlier version and has data in the cut columns.
    [ObservableProperty] private bool _showLegacyFields;

    // v2.18 (schema v5): one-tap focus adherence. -1 = unanswered,
    // 0 = no, 1 = partly, 2 = yes. Asked against the session intention
    // declared at Start Block for the day this game was played.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusAdherenceYes))]
    [NotifyPropertyChangedFor(nameof(FocusAdherencePartly))]
    [NotifyPropertyChangedFor(nameof(FocusAdherenceNo))]
    private int _focusAdherence = -1;

    public bool FocusAdherenceYes => FocusAdherence == 2;
    public bool FocusAdherencePartly => FocusAdherence == 1;
    public bool FocusAdherenceNo => FocusAdherence == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessionIntentionForGame))]
    private string _sessionIntentionForGame = "";

    public bool HasSessionIntentionForGame => !string.IsNullOrWhiteSpace(SessionIntentionForGame);

    // v2.18: rank benchmark context rendered under the stat cards — the
    // app's answer to "is this number bad?". Approximate per-rank averages
    // for the role this game was played in.
    [ObservableProperty] private string _benchmarkRankLine = "";
    [ObservableProperty] private string _benchmarkNextLine = "";
    [ObservableProperty] private bool _hasBenchmarks;

    // v2.18 (schema v5): laning numbers from the Match-V5 timeline backfill.
    // Empty until the backfill has run for this game.
    [ObservableProperty] private string _laningAt10Line = "";
    [ObservableProperty] private bool _hasLaningAt10;

    private void ApplyBenchmarks(ReviewScreenData screenData)
    {
        var rank = Revu.Core.Services.RankBenchmarks.NormalizeRank(screenData.BenchmarkRank);
        var role = Revu.Core.Services.RankBenchmarks.NormalizeRole(screenData.Game.Position);
        var current = Revu.Core.Services.RankBenchmarks.Get(screenData.Game.Position, rank);
        if (role.Length == 0 || current is null)
        {
            BenchmarkRankLine = "";
            BenchmarkNextLine = "";
            HasBenchmarks = false;
            return;
        }

        var nextRank = Revu.Core.Services.RankBenchmarks.NextRank(rank);
        var next = Revu.Core.Services.RankBenchmarks.Get(screenData.Game.Position, nextRank);

        BenchmarkRankLine = $"~{rank} {role} AVG // {Revu.Core.Services.RankBenchmarks.FormatLine(current)}";
        BenchmarkNextLine = next is not null && !string.Equals(nextRank, rank, StringComparison.Ordinal)
            ? $"~{nextRank} TARGET // {Revu.Core.Services.RankBenchmarks.FormatLine(next)}"
            : "";
        HasBenchmarks = true;
    }

    // v2.18 (schema v5): death audit — one row per DEATH event from the live
    // kill feed, each with six one-tap cause chips. The mix over a block of
    // games turns "I die too much" into "44% of my deaths are vision-class".
    public ObservableCollection<DeathAuditItem> DeathAudit { get; } = new();

    [ObservableProperty] private bool _hasDeathAudit;

    private async Task LoadDeathAuditAsync(long gameId)
    {
        try
        {
            var events = await _eventsRepository.GetEventsAsync(gameId);
            var deaths = events
                .Where(static e => string.Equals(e.EventType, Revu.Core.Models.GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static e => e.GameTimeS)
                .ToList();

            var saved = await _deathClassificationsRepository.GetForGameAsync(gameId);
            var savedByTime = saved
                .GroupBy(static c => c.GameTimeSeconds)
                .ToDictionary(static g => g.Key, static g => g.First().DeathClass);

            DispatcherHelper.RunOnUIThread(() =>
            {
                DeathAudit.Clear();
                foreach (var death in deaths)
                {
                    var item = new DeathAuditItem
                    {
                        GameId = gameId,
                        GameTimeSeconds = death.GameTimeS,
                    };
                    savedByTime.TryGetValue(death.GameTimeS, out var selectedClass);
                    foreach (var (key, label, hint) in Revu.Core.Data.Repositories.DeathClasses.All)
                    {
                        item.Chips.Add(new DeathChipOption
                        {
                            GameId = gameId,
                            GameTimeSeconds = death.GameTimeS,
                            Key = key,
                            Label = label,
                            Hint = hint,
                            IsSelected = string.Equals(selectedClass, key, StringComparison.OrdinalIgnoreCase),
                        });
                    }
                    DeathAudit.Add(item);
                }
                HasDeathAudit = DeathAudit.Count > 0;
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load death audit for game {GameId}", gameId);
            DispatcherHelper.RunOnUIThread(() =>
            {
                DeathAudit.Clear();
                HasDeathAudit = false;
            });
        }
    }

    /// <summary>One-tap death classification: select persists, re-tap clears.</summary>
    public void ClassifyDeath(DeathChipOption chip)
    {
        var item = DeathAudit.FirstOrDefault(d => d.GameTimeSeconds == chip.GameTimeSeconds);
        if (item is null)
        {
            return;
        }

        if (chip.IsSelected)
        {
            chip.IsSelected = false;
            BackgroundTaskRunner.Run(
                () => _deathClassificationsRepository.ClearAsync(chip.GameId, chip.GameTimeSeconds),
                _logger,
                $"death class clear {chip.GameId}@{chip.GameTimeSeconds}");
            return;
        }

        foreach (var sibling in item.Chips)
        {
            sibling.IsSelected = sibling == chip;
        }

        BackgroundTaskRunner.Run(
            () => _deathClassificationsRepository.UpsertAsync(chip.GameId, chip.GameTimeSeconds, chip.Key),
            _logger,
            $"death class {chip.Key} {chip.GameId}@{chip.GameTimeSeconds}");
    }

    [RelayCommand]
    private void SetFocusAdherence(string? value)
    {
        if (!int.TryParse(value, out var parsed))
        {
            return;
        }

        // Tapping the already-selected answer clears back to unanswered.
        FocusAdherence = FocusAdherence == parsed ? -1 : Math.Clamp(parsed, 0, 2);

        // Persist immediately — the row already exists for auto-captured games;
        // the review-save path re-stamps it for everything else.
        var gameId = GameId;
        var adherence = FocusAdherence < 0 ? (int?)null : FocusAdherence;
        if (gameId > 0)
        {
            BackgroundTaskRunner.Run(
                () => _sessionLogRepository.UpdateFocusAdherenceAsync(gameId, adherence),
                _logger,
                $"focus adherence {gameId}");
        }
    }

    public ObservableCollection<ConceptTagItem> AllTags { get; } = new();
    public ObservableCollection<long> SelectedTagIds { get; } = new();
    public ObservableCollection<ObjectiveAssessment> ObjectiveAssessments { get; } = new();
    public ObservableCollection<MatchupHistoryItem> MatchupHistory { get; } = new();
    public ObservableCollection<EvidenceInboxItem> EvidenceItems { get; } = new();
    public ObservableCollection<EvidenceInboxItem> UnassignedEvidenceItems { get; } = new();
    public ObservableCollection<ObjectiveOption> EvidenceObjectiveOptions { get; } = new();
    public ObservableCollection<TagOption> EvidenceTagOptions { get; } = new();

    public bool HasUnassignedEvidenceItems => UnassignedEvidenceItems.Count > 0;

    public static IReadOnlyList<string> AttributionOptions { get; } =
    [
        "My play",
        "Team effort",
        "Teammates",
        "External"
    ];

    // v2.16 role→champion map + the role played this game, captured at game end.
    // Drive the role-aware 2v2 matchup title (ADC+supp, mid+jg; top stays 1v1).
    private string _participantMapJson = "";
    private string _gameRole = "";

    public string MatchupHeading
    {
        get
        {
            if (!HasEnemyLaner && string.IsNullOrWhiteSpace(_participantMapJson))
                return $"{ChampionName} matchup notes";

            // Shared with the games-list pill so the title matches everywhere:
            // expands to the role pairing when the map is available, else 1v1.
            return Revu.Core.Services.MatchupDisplay.Build(
                ChampionName, EnemyLaner, _gameRole, _participantMapJson);
        }
    }

    public ReviewViewModel(
        IReviewWorkflowService reviewWorkflowService,
        INavigationService navigationService,
        IPromptsRepository promptsRepository,
        IObjectivesRepository objectivesRepository,
        IVodRepository vodRepository,
        IGameEventsRepository eventsRepository,
        IEvidenceRepository evidenceRepository,
        IReviewExportService reviewExportService,
        ISessionLogRepository sessionLogRepository,
        IDeathClassificationsRepository deathClassificationsRepository,
        IMessenger messenger,
        ILogger<ReviewViewModel> logger)
    {
        _reviewWorkflowService = reviewWorkflowService;
        _navigationService = navigationService;
        _promptsRepository = promptsRepository;
        _objectivesRepository = objectivesRepository;
        _vodRepository = vodRepository;
        _eventsRepository = eventsRepository;
        _evidenceRepository = evidenceRepository;
        _reviewExportService = reviewExportService;
        _sessionLogRepository = sessionLogRepository;
        _deathClassificationsRepository = deathClassificationsRepository;
        _messenger = messenger;
        _logger = logger;
        _messenger.RegisterAll(this);
    }

    /// <summary>v2.16.6: when a bookmark/clip is added, retagged, or its
    /// note is edited in the VOD player, re-merge into the per-objective
    /// notes / spotted-problems on the live review form. No-op when the
    /// event is for a different game (e.g. user has another VOD open).
    /// v2.16.7: also re-pull game_objectives so the "Practiced" toggle
    /// reflects the auto-flip that VodPlayerVM does when a bookmark gets
    /// tagged to an objective.</summary>
    public void Receive(Revu.Core.Lcu.BookmarkChangedMessage message)
    {
        if (message.GameId != GameId) return;
        BackgroundTaskRunner.Run(() => Helpers.DispatcherHelper.RunOnUIThreadAsync(async () =>
        {
            try
            {
                await AutoPopulateBookmarkNotesAsync(GameId);
                await SyncPracticedFlagsAsync(GameId);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Live bookmark auto-populate failed"); }
        }), _logger, $"live bookmark refresh {message.GameId}");
    }

    /// <summary>v2.16.7: re-read game_objectives and update the live
    /// ObjectiveAssessment rows' Practiced flag in place so the toggle
    /// pip flips on without forcing a page reload.</summary>
    private async Task SyncPracticedFlagsAsync(long gameId)
    {
        try
        {
            var rows = await _objectivesRepository.GetGameObjectivesAsync(gameId);
            var byId = rows.ToDictionary(r => r.ObjectiveId, r => r);
            foreach (var assessment in ObjectiveAssessments)
            {
                if (byId.TryGetValue(assessment.ObjectiveId, out var row))
                {
                    if (assessment.Practiced != row.Practiced) assessment.Practiced = row.Practiced;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Practiced-flag sync failed for game {GameId}", gameId);
        }
    }

    [RelayCommand]
    private async Task LoadAsync(long gameId)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        using var perf = PerformanceTrace.Time("Review.Load", $"gameId={gameId}");
        _vodRetryCts?.Cancel();
        ClearValidation();

        try
        {
            var screenData = await _reviewWorkflowService.LoadAsync(gameId);
            if (screenData is null)
            {
                _logger.LogWarning("Game {GameId} not found", gameId);
                SetValidation("Failed to load review data for this game.");
                return;
            }

            GameId = gameId;
            ApplyGameData(screenData);
            ApplySnapshot(screenData.Snapshot);
            ApplyTags(screenData.Tags);
            ApplyObjectives(screenData.ObjectiveAssessments, screenData.PriorityObjective);
            ApplyMatchupHistory(screenData.MatchupHistory);
            SessionIntentionForGame = screenData.SessionIntention;
            ApplyBenchmarks(screenData);

            // v2.15.0: legacy-field visibility is recomputed from the already-
            // loaded snapshot (SelfAssessment etc. live in Snapshot). True when
            // any of the v2.14-and-earlier review fields are populated.
            ShowLegacyFields = !string.IsNullOrWhiteSpace(WentWell)
                            || !string.IsNullOrWhiteSpace(Mistakes)
                            || !string.IsNullOrWhiteSpace(FocusNext)
                            || !string.IsNullOrWhiteSpace(OutsideControl)
                            || !string.IsNullOrWhiteSpace(WithinControl)
                            || !string.IsNullOrWhiteSpace(Attribution)
                            || !string.IsNullOrWhiteSpace(PersonalContribution)
                            || !string.IsNullOrWhiteSpace(ImprovementNote);

            // v2.18 (digest 2026-06-11-2 P3a): the reappraisal section is
            // collapsed by default (opt-in cost), but saved text must never
            // hide — auto-expand when either field already has content.
            IsReappraisalExpanded = !string.IsNullOrWhiteSpace(OutsideControl)
                                 || !string.IsNullOrWhiteSpace(WithinControl);

            // v2.15.0: load custom prompts for each post-phase assessment and
            // hydrate previously-saved answers for this game.
            await HydratePromptsAsync(gameId);

            // v2.18 (schema v5): death audit rows from the live kill feed.
            await LoadDeathAuditAsync(gameId);

            // v2.15.0: bookmark/clip autopopulate — injects [MM:SS] lines into
            // each assessment's general-notes field from bookmarks/clips
            // assigned to that objective on this game.
            await AutoPopulateBookmarkNotesAsync(gameId);

            EvidenceItems.Clear();
            HasEvidenceItems = false;
            IsEvidenceLoading = true;
            BackgroundTaskRunner.Run(async () =>
            {
                try
                {
                    await LoadReviewEvidenceAsync(gameId);
                }
                finally
                {
                    DispatcherHelper.RunOnUIThread(() => IsEvidenceLoading = false);
                }
            }, _logger, $"load review evidence {gameId}");

            RequireReviewNotes = screenData.RequireReviewNotes;
            HasVod = screenData.HasVod;
            BookmarkCount = screenData.BookmarkCount;
            UpdateSaveBehaviorText();

            if (!HasVod)
            {
                ScheduleVodRecheck(gameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load review for game {GameId}", gameId);
            SetValidation("Failed to load review data for this game.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// v2.15.0: load in-game + post-game custom prompts for each active
    /// objective assessment, hydrate previously-saved answers for
    /// <paramref name="gameId"/>, and apply champion-gating to filter out
    /// objectives that don't apply to this game's champion (unless the user
    /// already interacted with them — we never hide data they wrote).
    /// Non-fatal — prompts UI just hides on failure.
    /// </summary>
    private async Task HydratePromptsAsync(long gameId)
    {
        try
        {
            // Pull saved answers once; group by prompt id for O(1) lookup below.
            var saved = await _promptsRepository.GetAnswersForGameAsync(gameId);
            var answersByPromptId = saved.ToDictionary(a => a.PromptId, a => a.AnswerText);

            // Track which assessments to drop due to champion mismatch.
            // Never drop an assessment the user already touched: practiced
            // toggle, execution note, or an existing prompt answer.
            var toRemove = new List<ObjectiveAssessment>();
            var currentChampion = (ChampionName ?? "").Trim();

            foreach (var assessment in ObjectiveAssessments)
            {
                var prompts = await _promptsRepository.GetPromptsForObjectiveAsync(assessment.ObjectiveId);
                assessment.Prompts.Clear();
                bool hasAnyAnswerForThisGame = false;
                // v2.16.2 / v2.17.26: include PreGame prompts so the user can see
                // what they committed to before queueing AND finish/revise them
                // after the game (champ select is short — see PreGamePage timing).
                // Sort pre-game first so it reads top-down: "here's what I said
                // pre-game → here's how I'll answer post-game."
                var ordered = prompts
                    .Where(p => p.Phase == ObjectivePhases.PreGame
                             || p.Phase == ObjectivePhases.InGame
                             || p.Phase == ObjectivePhases.PostGame)
                    .OrderBy(p => p.Phase == ObjectivePhases.PreGame ? 0
                               : p.Phase == ObjectivePhases.InGame ? 1
                               : 2);

                foreach (var p in ordered)
                {
                    var text = answersByPromptId.TryGetValue(p.Id, out var t) ? t : "";

                    if (!string.IsNullOrWhiteSpace(text)) hasAnyAnswerForThisGame = true;

                    assessment.Prompts.Add(new PromptAnswerField
                    {
                        PromptId = p.Id,
                        Phase = p.Phase,
                        Label = p.Label,
                        AnswerText = text,
                    });
                }

                // Apply champion-gate filter — but only if the user hasn't
                // already touched this assessment for this game.
                if (!string.IsNullOrWhiteSpace(currentChampion))
                {
                    var userTouchedIt = assessment.Practiced
                                       || !string.IsNullOrWhiteSpace(assessment.ExecutionNote)
                                       || hasAnyAnswerForThisGame;
                    if (!userTouchedIt)
                    {
                        var champs = await _objectivesRepository.GetChampionsForObjectiveAsync(assessment.ObjectiveId);
                        if (champs.Count > 0
                            && !champs.Any(c => string.Equals(c, currentChampion, StringComparison.OrdinalIgnoreCase)))
                        {
                            toRemove.Add(assessment);
                        }
                    }
                }
            }

            foreach (var ass in toRemove) ObjectiveAssessments.Remove(ass);
            HasObjectives = ObjectiveAssessments.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to hydrate custom prompts for game {GameId}", gameId);
        }
    }

    /// <summary>
    /// v2.15.0: for each <see cref="ObjectiveAssessment"/>, format any bookmarks
    /// + clips assigned to it (on this game) as <c>[MM:SS] note</c> lines and
    /// merge them into the assessment's <c>ExecutionNote</c>. Never overwrites
    /// user content — uses substring detection to avoid re-appending on reload.
    /// </summary>
    private async Task AutoPopulateBookmarkNotesAsync(long gameId)
    {
        try
        {
            var bookmarks = await _vodRepository.GetBookmarksAsync(gameId);
            if (bookmarks.Count == 0) return;

            // v2.15.7: prompt-tagged clips/bookmarks route into the answer
            // field of the specific prompt, not the parent objective's general
            // notes. Routing is owned by the bookmark row's prompt_id, set by
            // the VodPlayer's tag picker. Objective-only tags fall through to
            // ExecutionNote as before.
            foreach (var assessment in ObjectiveAssessments)
            {
                foreach (var promptField in assessment.Prompts)
                {
                    var forPrompt = bookmarks
                        .Where(b => b.PromptId == promptField.PromptId)
                        .OrderBy(b => b.GameTimeSeconds)
                        .ToList();
                    if (forPrompt.Count == 0) continue;
                    AppendBookmarkLines(forPrompt, _ => promptField.AnswerText ?? "",
                                        t => promptField.AnswerText = t);
                }

                // v2.15.7: clips that USED to be objective-level but are now
                // prompt-tagged need their old [MM:SS] line removed from
                // General Notes — otherwise the line appears in both places
                // (objective notes + the new prompt answer). Match by timestamp;
                // a tiny risk of scrubbing a hand-typed line at the same MM:SS
                // is acceptable here because we explicitly opted into this.
                var promptTaggedHere = bookmarks
                    .Where(b => b.ObjectiveId == assessment.ObjectiveId && b.PromptId is not null)
                    .Select(b => FormatGameTime(b.GameTimeSeconds))
                    .ToHashSet();
                if (promptTaggedHere.Count > 0
                    && !string.IsNullOrEmpty(assessment.ExecutionNote))
                {
                    assessment.ExecutionNote = ScrubAutoLines(assessment.ExecutionNote, promptTaggedHere);
                }

                // Per-objective bookmarks (no prompt) → that objective's general notes.
                var objLevel = bookmarks
                    .Where(b => b.ObjectiveId == assessment.ObjectiveId && b.PromptId is null)
                    .OrderBy(b => b.GameTimeSeconds)
                    .ToList();
                if (objLevel.Count == 0) continue;
                assessment.ExecutionNote = ScrubExactBookmarkLines(assessment.ExecutionNote ?? "", objLevel);
            }

            // v2.15.5: bookmarks without an objective (bulk of the user's
            // existing data — most bookmarks shipped untagged) go into the
            // "anything else you noticed" box so they're not invisible.
            var untagged = bookmarks
                .Where(b => b.ObjectiveId is null && b.PromptId is null)
                .OrderBy(b => b.GameTimeSeconds)
                .ToList();
            if (untagged.Count > 0)
            {
                AppendBookmarkLines(untagged, _ => SpottedProblems ?? "",
                                    t => SpottedProblems = t);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bookmark autopopulate failed for game {GameId}", gameId);
        }
    }

    private async Task LoadReviewEvidenceAsync(long gameId)
    {
        using var perf = PerformanceTrace.Time("Review.LoadEvidence", $"gameId={gameId}");
        try
        {
            var objectives = await _objectivesRepository.GetActiveAsync();
            var tagRows = new List<TagOption>
            {
                new()
                {
                    Kind = TagOption.OptionKind.None,
                    Title = "(no objective)",
                    SearchText = "none no objective clear",
                },
            };

            foreach (var objective in objectives)
            {
                tagRows.Add(new TagOption
                {
                    Kind = TagOption.OptionKind.Objective,
                    ObjectiveId = objective.Id,
                    Title = objective.Title,
                    SearchText = objective.Title,
                });

            }

            var objectiveOptions = objectives
                .Select(static objective => new ObjectiveOption(objective.Id, objective.Title))
                .Prepend(new ObjectiveOption(null, "(no objective)"))
                .ToArray();

            DispatcherHelper.RunOnUIThread(() =>
            {
                EvidenceObjectiveOptions.Clear();
                foreach (var option in objectiveOptions)
                {
                    EvidenceObjectiveOptions.Add(option);
                }

                EvidenceTagOptions.Clear();
                foreach (var row in tagRows)
                {
                    EvidenceTagOptions.Add(row);
                }
            });

            var events = await _eventsRepository.GetEventsAsync(gameId);
            foreach (var inferred in TimelineInferenceService.Infer(events)
                         .Where(static region => region.Priority >= 50)
                         .Take(MaxAutoTimelineEvidence))
            {
                await _evidenceRepository.UpsertAsync(new EvidenceUpsert(
                    GameId: gameId,
                    SourceKind: EvidenceKinds.TimelineRegion,
                    SourceId: null,
                    SourceKey: inferred.SourceKey,
                    StartTimeSeconds: inferred.StartTimeSeconds,
                    EndTimeSeconds: inferred.EndTimeSeconds,
                    Title: inferred.Name,
                    Note: inferred.Tooltip,
                    Polarity: InferEvidencePolarity(inferred.Name)));
            }

            var bookmarks = await _vodRepository.GetBookmarksAsync(gameId);
            foreach (var clip in bookmarks.Where(static b => b.ClipStartSeconds is not null || !string.IsNullOrWhiteSpace(b.ClipPath)))
            {
                var polarity = NormalizeEvidencePolarity(clip.Quality);
                await _evidenceRepository.UpsertAsync(new EvidenceUpsert(
                    GameId: gameId,
                    SourceKind: EvidenceKinds.Clip,
                    SourceId: clip.Id,
                    SourceKey: $"clip:{clip.Id}",
                    StartTimeSeconds: clip.ClipStartSeconds ?? clip.GameTimeSeconds,
                    EndTimeSeconds: clip.ClipEndSeconds ?? clip.GameTimeSeconds,
                    Title: string.IsNullOrWhiteSpace(clip.Note) ? "Saved clip" : clip.Note.Trim(),
                    Note: clip.Note,
                    ObjectiveId: clip.ObjectiveId,
                    Polarity: polarity,
                    Status: polarity == EvidencePolarities.Neutral ? EvidenceStatuses.NeedsReview : EvidenceStatuses.Evidence));
            }

            var rows = PrioritizeEvidenceRows(await _evidenceRepository.GetForGameAsync(gameId));
            DispatcherHelper.RunOnUIThread(() =>
            {
                EvidenceItems.Clear();
                foreach (var row in rows)
                {
                    EvidenceItems.Add(new EvidenceInboxItem
                    {
                        Id = row.Id,
                        GameId = row.GameId,
                        SourceKind = row.SourceKind,
                        SourceId = row.SourceId,
                        SourceKey = row.SourceKey,
                        StartTimeSeconds = row.StartTimeSeconds,
                        EndTimeSeconds = row.EndTimeSeconds,
                        Title = row.Title,
                        Note = row.Note,
                        ObjectiveId = row.ObjectiveId,
                        Polarity = row.Polarity,
                        Status = row.Status,
                        ObjectiveOptions = EvidenceObjectiveOptions,
                        TagOptions = EvidenceTagOptions,
                    });
                }

                HasEvidenceItems = EvidenceItems.Count > 0;
                AttachEvidenceToObjectives();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load review evidence for game {GameId}", gameId);
            HasEvidenceItems = false;
        }
    }

    private static IReadOnlyList<EvidenceItemRecord> PrioritizeEvidenceRows(IReadOnlyList<EvidenceItemRecord> rows)
    {
        return rows
            .Where(static row => row.SourceKind == EvidenceKinds.Clip
                || row.ObjectiveId.HasValue
                || row.Status is EvidenceStatuses.Evidence or EvidenceStatuses.Highlight)
            .OrderByDescending(static row => row.ObjectiveId.HasValue)
            .ThenByDescending(static row => row.SourceKind == EvidenceKinds.Clip)
            .ThenBy(static row => row.Status == EvidenceStatuses.NeedsReview ? 0 : 1)
            .ThenBy(static row => row.Polarity == EvidencePolarities.Bad ? 0
                : row.Polarity == EvidencePolarities.Good ? 1
                : 2)
            .ThenBy(static row => row.StartTimeSeconds ?? int.MaxValue)
            .Take(MaxEvidenceInboxItems)
            .ToArray();
    }

    private void AttachEvidenceToObjectives()
    {
        var byObjective = EvidenceItems
            .Where(static item => item.ObjectiveId.HasValue)
            .GroupBy(static item => item.ObjectiveId!.Value)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static item => item.StartTimeSeconds ?? int.MaxValue)
                    .ThenBy(static item => item.Id)
                    .ToArray());

        foreach (var assessment in ObjectiveAssessments)
        {
            assessment.ReplaceEvidenceItems(
                byObjective.TryGetValue(assessment.ObjectiveId, out var items)
                    ? items
                    : []);
        }

        UnassignedEvidenceItems.Clear();
        foreach (var item in EvidenceItems
                     .Where(static item => !item.ObjectiveId.HasValue)
                     .OrderBy(static item => item.StartTimeSeconds ?? int.MaxValue)
                     .ThenBy(static item => item.Id))
        {
            UnassignedEvidenceItems.Add(item);
        }

        OnPropertyChanged(nameof(HasUnassignedEvidenceItems));
    }

    private async Task MarkObjectivePracticedFromEvidenceAsync(long objectiveId)
    {
        var existing = await _objectivesRepository.GetGameObjectivesAsync(GameId);
        var existingRow = existing.FirstOrDefault(row => row.ObjectiveId == objectiveId);
        if (existingRow?.Practiced == true)
        {
            return;
        }

        await _objectivesRepository.RecordGameAsync(
            GameId,
            objectiveId,
            practiced: true,
            executionNote: existingRow?.ExecutionNote ?? "");
    }

    /// <summary>
    /// v2.15.5: shared append logic for autopopulating bookmark lines into a
    /// free-text field. Uses substring detection to avoid re-appending on
    /// reload, and appends with a "— Bookmarks/clips —" separator header
    /// when the target already has user-typed content.
    /// </summary>
    private static void AppendBookmarkLines(
        IReadOnlyList<Revu.Core.Data.Repositories.VodBookmarkRecord> bookmarks,
        Func<string, string> getCurrent,
        Action<string> setNext)
    {
        var lines = new List<string>(bookmarks.Count);
        foreach (var bm in bookmarks)
        {
            var ts = FormatGameTime(bm.GameTimeSeconds);
            var note = (bm.Note ?? "").Trim();
            var clipTag = (bm.ClipStartSeconds.HasValue && bm.ClipEndSeconds.HasValue)
                ? $" (clip {FormatGameTime(bm.ClipStartSeconds.Value)}–{FormatGameTime(bm.ClipEndSeconds.Value)})"
                : "";
            var line = string.IsNullOrEmpty(note)
                ? $"[{ts}]{clipTag}"
                : $"[{ts}] {note}{clipTag}";
            lines.Add(line);
        }

        var currentNote = getCurrent("");
        var toAdd = lines.Where(l => !currentNote.Contains(l, StringComparison.Ordinal)).ToList();
        if (toAdd.Count == 0) return;

        var additionBlock = string.Join("\n", toAdd);
        if (string.IsNullOrWhiteSpace(currentNote))
        {
            setNext(additionBlock);
        }
        else
        {
            var separator = currentNote.TrimEnd().EndsWith("— Bookmarks/clips —", StringComparison.Ordinal)
                ? "\n"
                : "\n\n— Bookmarks/clips —\n";
            setNext(currentNote + separator + additionBlock);
        }
    }

    private static string FormatGameTime(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }

    private static string NormalizeEvidencePolarity(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is EvidencePolarities.Good or EvidencePolarities.Bad
            ? normalized
            : EvidencePolarities.Neutral;
    }

    private static string InferEvidencePolarity(string title)
    {
        var normalized = (title ?? "").Trim().ToLowerInvariant();
        if (normalized.StartsWith("won ", StringComparison.Ordinal)
            || normalized.Contains(" pick", StringComparison.Ordinal))
        {
            return EvidencePolarities.Good;
        }

        if (normalized.StartsWith("lost ", StringComparison.Ordinal)
            || normalized.Contains("death", StringComparison.Ordinal))
        {
            return EvidencePolarities.Bad;
        }

        return EvidencePolarities.Neutral;
    }

    /// <summary>
    /// v2.15.7: drop any line that begins with <c>[MM:SS]</c> for a timestamp
    /// in <paramref name="timestamps"/>. Used to evict stale auto-populated
    /// clip lines from General Notes after a clip is re-tagged to a prompt.
    /// </summary>
    private static string ScrubAutoLines(string note, HashSet<string> timestamps)
    {
        var lines = note.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            bool drop = false;
            foreach (var ts in timestamps)
            {
                if (raw.StartsWith($"[{ts}]", StringComparison.Ordinal))
                {
                    drop = true;
                    break;
                }
            }
            if (!drop) kept.Add(raw);
        }
        // Also drop a trailing dangling "— Bookmarks/clips —" header if we
        // emptied everything below it.
        while (kept.Count > 0
               && kept[^1].TrimEnd().EndsWith("— Bookmarks/clips —", StringComparison.Ordinal))
        {
            kept.RemoveAt(kept.Count - 1);
        }
        return string.Join("\n", kept).TrimEnd('\n');
    }

    private static string ScrubExactBookmarkLines(
        string note,
        IReadOnlyList<Revu.Core.Data.Repositories.VodBookmarkRecord> bookmarks)
    {
        if (string.IsNullOrWhiteSpace(note) || bookmarks.Count == 0)
        {
            return note;
        }

        var bookmarkNotesByTimestamp = bookmarks
            .GroupBy(static bm => FormatGameTime(bm.GameTimeSeconds))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static bm => (bm.Note ?? "").Trim())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray());

        var kept = new List<string>();
        foreach (var raw in note.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var shouldDrop = false;

            foreach (var (timestamp, notes) in bookmarkNotesByTimestamp)
            {
                if (!line.StartsWith($"[{timestamp}]", StringComparison.Ordinal))
                {
                    continue;
                }

                shouldDrop = notes.Length == 0
                    || notes.Any(noteText => line.Contains(noteText, StringComparison.Ordinal));
                if (shouldDrop)
                {
                    break;
                }
            }

            if (!shouldDrop)
            {
                kept.Add(raw);
            }
        }

        while (kept.Count > 0
               && kept[^1].TrimEnd().EndsWith("â€” Bookmarks/clips â€”", StringComparison.Ordinal))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return string.Join("\n", kept).TrimEnd('\n');
    }

    /// <summary>
    /// v2.15.0: persist current prompt answers for each assessment. Runs after
    /// SaveCoreAsync's main write path. Empty/whitespace answers delete the row.
    /// </summary>
    private async Task PersistPromptAnswersAsync(long gameId)
    {
        foreach (var assessment in ObjectiveAssessments)
        {
            foreach (var field in assessment.Prompts)
            {
                try
                {
                    await _promptsRepository.SaveAnswerAsync(field.PromptId, gameId, field.AnswerText ?? "");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to persist prompt answer game={GameId} prompt={PromptId}",
                        gameId, field.PromptId);
                }
            }
        }
    }

    [RelayCommand]
    private async Task SaveReviewAsync()
    {
        await SaveCoreAsync(navigateBackOnSuccess: true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _vodRetryCts?.Cancel();
        _navigationService.NavigateTo("games");
    }

    [RelayCommand]
    private async Task WatchVodAsync()
    {
        var saved = await _reviewWorkflowService.SaveDraftAsync(new ReviewDraftRequest(GameId, BuildSnapshot()));
        if (!saved)
        {
            SetValidation("Couldn't preserve your review draft before opening the VOD.");
            return;
        }

        _navigationService.NavigateTo("vodplayer", GameId);
    }

    /// <summary>v2.18 (P-006): jump the VOD to a specific death so its cause
    /// can be tagged from what actually happened instead of from memory.
    /// ~10s of lead-in shows the mistake forming, not just the death.</summary>
    [RelayCommand]
    private async Task OpenDeathInVodAsync(DeathAuditItem? death)
    {
        if (death is null || GameId <= 0 || !HasVod)
        {
            return;
        }

        var saved = await _reviewWorkflowService.SaveDraftAsync(new ReviewDraftRequest(GameId, BuildSnapshot()));
        if (!saved)
        {
            SetValidation("Couldn't preserve your review draft before opening the VOD.");
            return;
        }

        _navigationService.NavigateTo("vodplayer", new VodPlayerNavigationRequest
        {
            GameId = GameId,
            SeekTimeS = Math.Max(0, death.GameTimeSeconds - DeathJumpPreRollSeconds),
        });
    }

    [RelayCommand]
    private async Task OpenEvidenceInVodAsync(EvidenceInboxItem? evidence)
    {
        if (evidence is null || GameId <= 0)
        {
            return;
        }

        var saved = await _reviewWorkflowService.SaveDraftAsync(new ReviewDraftRequest(GameId, BuildSnapshot()));
        if (!saved)
        {
            SetValidation("Couldn't preserve your review draft before opening the VOD.");
            return;
        }

        _navigationService.NavigateTo("vodplayer", new VodPlayerNavigationRequest
        {
            GameId = GameId,
            SeekTimeS = evidence.StartTimeSeconds is int start
                ? Math.Max(0, start - EvidenceJumpPreRollSeconds)
                : null,
        });
    }

    [RelayCommand]
    private async Task SetEvidenceStatusAsync(EvidenceStatusUpdateRequest? request)
    {
        if (request?.Evidence is not EvidenceInboxItem evidence || evidence.Id <= 0)
        {
            return;
        }

        var status = EvidenceStatuses.Normalize(request.Status);
        try
        {
            await _evidenceRepository.UpdateStatusAsync(evidence.Id, status);
            if (status == EvidenceStatuses.Dismissed)
            {
                EvidenceItems.Remove(evidence);
                HasEvidenceItems = EvidenceItems.Count > 0;
                AttachEvidenceToObjectives();
            }
            else
            {
                evidence.Status = status;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update evidence status {EvidenceId}", evidence.Id);
        }
    }

    [RelayCommand]
    private async Task SetEvidencePolarityAsync(EvidencePolarityUpdateRequest? request)
    {
        if (request?.Evidence is not EvidenceInboxItem evidence || evidence.Id <= 0)
        {
            return;
        }

        var polarity = EvidencePolarities.Normalize(request.Polarity);
        try
        {
            await _evidenceRepository.UpdatePolarityAsync(evidence.Id, polarity);
            evidence.Polarity = polarity;
            if (evidence.Status == EvidenceStatuses.NeedsReview)
            {
                await _evidenceRepository.UpdateStatusAsync(evidence.Id, EvidenceStatuses.Evidence);
                evidence.Status = EvidenceStatuses.Evidence;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update evidence polarity {EvidenceId}", evidence.Id);
        }
    }

    [RelayCommand]
    private async Task SetEvidenceObjectiveAsync(EvidenceObjectiveUpdateRequest? request)
    {
        if (request?.Evidence is not EvidenceInboxItem evidence || evidence.Id <= 0)
        {
            return;
        }

        var previous = evidence.ObjectiveId;
        evidence.ObjectiveId = request.ObjectiveId;
        try
        {
            await _evidenceRepository.UpdateObjectiveAsync(evidence.Id, request.ObjectiveId);
            if (request.ObjectiveId is long objectiveId)
            {
                if (evidence.Status == EvidenceStatuses.NeedsReview)
                {
                    await _evidenceRepository.UpdateStatusAsync(evidence.Id, EvidenceStatuses.Evidence);
                    evidence.Status = EvidenceStatuses.Evidence;
                }
                await MarkObjectivePracticedFromEvidenceAsync(objectiveId);
                await SyncPracticedFlagsAsync(GameId);
            }

            AttachEvidenceToObjectives();
        }
        catch (Exception ex)
        {
            evidence.ObjectiveId = previous;
            _logger.LogError(ex, "Failed to update evidence objective {EvidenceId}", evidence.Id);
        }
    }

    [RelayCommand]
    private async Task ExportReviewAsync()
    {
        if (IsExportingReview || GameId <= 0)
        {
            return;
        }

        IsExportingReview = true;
        ExportStatusText = "Building export...";

        try
        {
            var markdown = await _reviewExportService.ExportGameAsync(GameId);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                ExportStatusText = "Could not find this game.";
                return;
            }

            var champion = string.IsNullOrWhiteSpace(ChampionName)
                ? "game"
                : string.Join("", ChampionName.Trim().Replace(' ', '-').Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch));
            var fileName = $"revu-{GameId}-{champion}-review.md";
            var path = await MarkdownExportPicker.PickSavePathAsync(fileName);
            if (string.IsNullOrWhiteSpace(path))
            {
                ExportStatusText = "Export canceled.";
                return;
            }

            await File.WriteAllTextAsync(path, markdown);
            ExportStatusText = $"Exported to {path}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export review for game {GameId}", GameId);
            ExportStatusText = "Export failed. Check the logs and try again.";
        }
        finally
        {
            IsExportingReview = false;
        }
    }

    [RelayCommand]
    private void ToggleTag(ConceptTagItem tag)
    {
        tag.IsSelected = !tag.IsSelected;
        if (tag.IsSelected)
        {
            if (!SelectedTagIds.Contains(tag.Id))
            {
                SelectedTagIds.Add(tag.Id);
            }
        }
        else
        {
            SelectedTagIds.Remove(tag.Id);
        }
    }

    partial void OnMentalRatingChanged(int value)
    {
        UpdateMentalColor();
        ShowMentalReflection = value <= 3;
    }

    partial void OnRequireReviewNotesChanged(bool value)
    {
        UpdateSaveBehaviorText();
    }

    partial void OnEnemyLanerChanged(string value)
    {
        HasEnemyLaner = !string.IsNullOrWhiteSpace(value);
        OnPropertyChanged(nameof(MatchupHeading));

        if (!IsLoading && GameId > 0)
        {
            BackgroundTaskRunner.Run(
                () => LoadMatchupHistoryAsync(value.Trim()),
                _logger,
                $"load matchup history {GameId}");
        }
    }

    private async Task<bool> SaveCoreAsync(bool navigateBackOnSuccess)
    {
        try
        {
            var result = await _reviewWorkflowService.SaveAsync(new SaveReviewRequest(
                GameId: GameId,
                ChampionName: ChampionName,
                Win: Win,
                RequireReviewNotes: RequireReviewNotes,
                Snapshot: BuildSnapshot()));

            if (!result.Success)
            {
                SetValidation(result.ErrorMessage);
                return false;
            }

            EnemyLaner = result.SavedEnemyLaner;
            ClearValidation();

            // v2.15.0: persist custom prompt answers alongside the legacy save.
            // Non-fatal on error — the main review save already succeeded.
            await PersistPromptAnswersAsync(GameId);

            if (navigateBackOnSuccess)
            {
                // Land on the Session Logger (review queue) regardless
                // of how the user got here. Routes like Dashboard →
                // Review → VOD player → "Open review" → Save would
                // otherwise pop back to the VOD player, which feels
                // stuck in the review/VOD detour. Session logger is the
                // canonical review-queue page so the next game is one
                // click away.
                _navigationService.NavigateTo("games");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save review for game {GameId}", GameId);
            SetValidation("Failed to save review. Check the logs and try again.");
            return false;
        }
    }

    private async Task LoadMatchupHistoryAsync(string enemyLaner)
    {
        try
        {
            var history = await _reviewWorkflowService.GetMatchupHistoryAsync(ChampionName, enemyLaner, GameId);
            ApplyMatchupHistory(history);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load matchup history for game {GameId}", GameId);
        }
    }

    private ReviewSnapshot BuildSnapshot()
    {
        return new ReviewSnapshot(
            MentalRating: MentalRating,
            WentWell: WentWell,
            Mistakes: Mistakes,
            FocusNext: FocusNext,
            ReviewNotes: ReviewNotes,
            ImprovementNote: ImprovementNote,
            Attribution: Attribution,
            MentalHandled: MentalHandled,
            SpottedProblems: SpottedProblems,
            OutsideControl: OutsideControl,
            WithinControl: WithinControl,
            PersonalContribution: PersonalContribution,
            EnemyLaner: EnemyLaner,
            MatchupNote: MatchupNote,
            SelectedTagIds: AllTags.Where(static tag => tag.IsSelected).Select(static tag => tag.Id).ToList(),
            ObjectivePractices: ObjectiveAssessments
                .Select(static assessment => new SaveObjectivePracticeRequest(
                    ObjectiveId: assessment.ObjectiveId,
                    Practiced: assessment.Practiced,
                    ExecutionNote: assessment.ExecutionNote))
                .ToList(),
            FocusAdherence: FocusAdherence < 0 ? null : FocusAdherence);
    }

    private void ApplyGameData(ReviewScreenData screenData)
    {
        var game = screenData.Game;
        ChampionName = game.ChampionName;
        // Feed the role-aware matchup title (2v2 for adc/supp/mid/jg, 1v1 top).
        _participantMapJson = game.ParticipantMap;
        _gameRole = game.Position;
        OnPropertyChanged(nameof(MatchupHeading));
        Win = game.Win;
        ResultText = game.Win ? "VICTORY" : "DEFEAT";
        ResultColorHex = game.Win ? AppSemanticPalette.PositiveHex : AppSemanticPalette.NegativeHex;
        KdaText = $"{game.Kills} / {game.Deaths} / {game.Assists}";
        KdaRatioText = $"{game.KdaRatio:F2} KDA";
        GameModeText = game.DisplayGameMode;
        DurationText = game.GameDuration > 0 ? $"{game.GameDuration / 60}:{game.GameDuration % 60:D2}" : "";
        HeaderText = $"Review -- {Revu.Core.Services.MatchupDisplay.Build(game.ChampionName, game.EnemyLaner, game.Position, game.ParticipantMap)} ({(game.Win ? "W" : "L")})";

        DamageText = FormatNumber(game.TotalDamageToChampions);
        CsText = game.CsTotal.ToString();
        CsPerMinText = $"{game.CsPerMin:F1}/m";
        VisionText = game.VisionScore.ToString();
        GoldText = FormatNumber(game.GoldEarned);
        KillParticipationText = game.KillParticipation > 0 ? $"{game.KillParticipation:F0}%" : "—";
        DamageTakenText = FormatNumber(game.TotalDamageTaken);
        WardsPlacedText = game.WardsPlaced.ToString();

        // v2.18 (schema v5): laning-at-10 from the timeline backfill. The
        // canonical "did I win lane?" numbers — gold/CS deltas vs the lane
        // opponent at the 10-minute mark.
        if (game.CsAt10 is { } csAt10)
        {
            var line = $"LANING @10 // CS {Revu.Core.Services.ObjectiveCriteria.FormatValue(csAt10)}";
            if (game.GoldDiffAt10 is { } goldDiff)
            {
                line += $" · GOLD DIFF {(goldDiff >= 0 ? "+" : "")}{goldDiff}";
            }
            if (game.CsDiffAt10 is { } csDiff)
            {
                line += $" · CS DIFF {(csDiff >= 0 ? "+" : "")}{Revu.Core.Services.ObjectiveCriteria.FormatValue(csDiff)}";
            }
            LaningAt10Line = line;
            HasLaningAt10 = true;
        }
        else
        {
            LaningAt10Line = "";
            HasLaningAt10 = false;
        }
    }

    private void ApplySnapshot(ReviewSnapshot snapshot)
    {
        MentalRating = snapshot.MentalRating;
        WentWell = snapshot.WentWell;
        Mistakes = snapshot.Mistakes;
        FocusNext = snapshot.FocusNext;
        ReviewNotes = snapshot.ReviewNotes;
        ImprovementNote = snapshot.ImprovementNote;
        Attribution = snapshot.Attribution;
        MentalHandled = snapshot.MentalHandled;
        SpottedProblems = snapshot.SpottedProblems;
        OutsideControl = snapshot.OutsideControl;
        WithinControl = snapshot.WithinControl;
        PersonalContribution = snapshot.PersonalContribution;
        EnemyLaner = snapshot.EnemyLaner;
        MatchupNote = snapshot.MatchupNote;
        FocusAdherence = snapshot.FocusAdherence ?? -1;
        UpdateMentalColor();
        ShowMentalReflection = MentalRating <= 3;
    }

    private void ApplyTags(IReadOnlyList<ReviewTagState> tags)
    {
        DispatcherHelper.RunOnUIThread(() =>
        {
            AllTags.Clear();
            SelectedTagIds.Clear();

            foreach (var tag in tags)
            {
                AllTags.Add(new ConceptTagItem
                {
                    Id = tag.Id,
                    Name = tag.Name,
                    Polarity = tag.Polarity,
                    ColorHex = tag.ColorHex,
                    IsSelected = tag.IsSelected
                });

                if (tag.IsSelected)
                {
                    SelectedTagIds.Add(tag.Id);
                }
            }
        });
    }

    private void ApplyObjectives(
        IReadOnlyList<ReviewObjectiveState> objectives,
        Revu.Core.Data.Repositories.ObjectiveSummary? priorityObjective)
    {
        DispatcherHelper.RunOnUIThread(() =>
        {
            ObjectiveAssessments.Clear();
            foreach (var objective in objectives)
            {
                ObjectiveAssessments.Add(new ObjectiveAssessment
                {
                    ObjectiveId = objective.ObjectiveId,
                    Title = objective.Title,
                    Criteria = objective.Criteria,
                    Phase = objective.Phase,
                    IsPriority = objective.IsPriority,
                    Practiced = objective.Practiced,
                    ExecutionNote = objective.ExecutionNote,
                    CriteriaVerdict = objective.CriteriaVerdict,
                    CriteriaVerdictSign = objective.CriteriaVerdictSign,
                    PracticedFromEvidence = objective.PracticedFromEvidence,
                });
            }

            HasObjectives = ObjectiveAssessments.Count > 0;
            PriorityObjectiveTitle = priorityObjective?.Title ?? "";
            PriorityObjectiveCriteria = priorityObjective?.CompletionCriteria ?? "";
            HasPriorityObjective = !string.IsNullOrWhiteSpace(PriorityObjectiveTitle);
        });
    }

    private void ApplyMatchupHistory(IReadOnlyList<ReviewMatchupHistoryItem> history)
    {
        var items = history
            .Select(item => new MatchupHistoryItem
            {
                Note = item.Note,
                Helpful = item.Helpful,
                MetaText = BuildMatchupMetaText(item)
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Note))
            .ToList();

        DispatcherHelper.RunOnUIThread(() =>
        {
            MatchupHistory.Clear();
            foreach (var item in items)
            {
                MatchupHistory.Add(item);
            }

            HasMatchupHistory = MatchupHistory.Count > 0;
        });
    }

    private void UpdateMentalColor()
    {
        MentalRatingColorHex = AppSemanticPalette.MentalRatingHex(MentalRating);
    }

    private void UpdateSaveBehaviorText()
    {
        SaveBehaviorText = RequireReviewNotes
            ? "Review notes are required before save."
            : "Review notes are optional. A blank save still marks the game reviewed.";
    }

    private void SetValidation(string message)
    {
        ValidationMessage = message;
        HasValidationMessage = !string.IsNullOrWhiteSpace(message);
    }

    private void ClearValidation()
    {
        ValidationMessage = "";
        HasValidationMessage = false;
    }

    private static string BuildMatchupMetaText(ReviewMatchupHistoryItem item)
    {
        var createdText = item.CreatedAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(item.CreatedAt.Value).LocalDateTime.ToString("MMM d, yyyy HH:mm")
            : "Unknown date";
        var helpfulText = item.Helpful switch
        {
            true => "Helpful",
            false => "Not helpful",
            null => "Unrated",
        };

        return item.GameId > 0
            ? $"Game {item.GameId}  •  {createdText}  •  {helpfulText}"
            : $"{createdText}  •  {helpfulText}";
    }

    private void ScheduleVodRecheck(long gameId)
    {
        _vodRetryCts?.Cancel();
        _vodRetryCts = new CancellationTokenSource();
        var token = _vodRetryCts.Token;

        BackgroundTaskRunner.Run(async () =>
        {
            // Retry a few times with increasing delays to catch recordings that
            // are still being finalized when the post-game page first loads.
            int[] delaysMs = [15_000, 30_000, 60_000];
            foreach (var delay in delaysMs)
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    var result = await _reviewWorkflowService.CheckVodAsync(gameId, token).ConfigureAwait(false);
                    if (result.HasVod)
                    {
                        Helpers.DispatcherHelper.RunOnUIThread(() =>
                        {
                            HasVod = true;
                            BookmarkCount = result.BookmarkCount;
                        });
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "VOD recheck failed for game {GameId}", gameId);
                }
            }
        }, _logger, $"VOD recheck {gameId}", token);
    }

    private static string FormatNumber(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString()
    };
}

/// <summary>Concept tag item with selection state for the tag selector.</summary>
public class ConceptTagItem : ObservableObject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Polarity { get; set; } = "neutral";

    private string _colorHex = AppSemanticPalette.AccentBlueHex;
    public string ColorHex
    {
        get => _colorHex;
        set
        {
            _colorHex = value;
            OnPropertyChanged(nameof(TagAccentBrush));
            OnPropertyChanged(nameof(TagBackgroundBrush));
            OnPropertyChanged(nameof(TagBorderBrush));
        }
    }

    public SolidColorBrush TagAccentBrush => AppSemanticPalette.TagAccentBrush(Polarity, ColorHex);
    public SolidColorBrush TagBackgroundBrush => IsSelected
        ? AppSemanticPalette.TagSurfaceBrush(Polarity, ColorHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.TagSurfaceHex);
    public SolidColorBrush TagBorderBrush => IsSelected
        ? AppSemanticPalette.TagAccentBrush(Polarity, ColorHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.SubtleBorderHex);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(TagBackgroundBrush));
                OnPropertyChanged(nameof(TagBorderBrush));
            }
        }
    }
}

/// <summary>Recent note for the same champion/enemy matchup.</summary>
public sealed class MatchupHistoryItem
{
    public string Note { get; init; } = "";
    public bool? Helpful { get; init; }
    public string MetaText { get; init; } = "";
}

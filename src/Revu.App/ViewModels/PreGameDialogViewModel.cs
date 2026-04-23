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

/// <summary>ViewModel for the pre-game focus dialog shown during champion select.</summary>
public partial class PreGameDialogViewModel : ObservableObject, IRecipient<ChampSelectUpdatedMessage>
{
    private readonly IGameRepository _gameRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IMatchupNotesRepository _matchupNotesRepo;
    private readonly IConfigService _configService;
    private readonly IMessenger _messenger;
    private readonly ILogger<PreGameDialogViewModel> _logger;

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

    [ObservableProperty]
    private bool _hasMatchupHistory;

    [ObservableProperty]
    private bool _hasPreGameObjectives;

    [ObservableProperty]
    private string _matchupHeaderText = "";

    // ── Detected matchup (even when no notes exist) ──────────────────

    /// <summary>Local player's locked-in champion (empty until lock or if only hovered).</summary>
    [ObservableProperty]
    private string _myChampionName = "";

    /// <summary>Enemy laner's locked-in champion (empty if enemy not locked, or no lane assignment e.g. ARAM).</summary>
    [ObservableProperty]
    private string _enemyChampionName = "";

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

    // ── Constructor ─────────────────────────────────────────────────

    public PreGameDialogViewModel(
        IGameRepository gameRepo,
        IObjectivesRepository objectivesRepo,
        ISessionLogRepository sessionLogRepo,
        IMatchupNotesRepository matchupNotesRepo,
        IConfigService configService,
        IMessenger messenger,
        ILogger<PreGameDialogViewModel> logger)
    {
        _gameRepo = gameRepo;
        _objectivesRepo = objectivesRepo;
        _sessionLogRepo = sessionLogRepo;
        _matchupNotesRepo = matchupNotesRepo;
        _configService = configService;
        _messenger = messenger;
        _logger = logger;
    }

    public void Attach() => _messenger.RegisterAll(this);

    public void Detach() => _messenger.UnregisterAll(this);

    public void Receive(ChampSelectUpdatedMessage message)
    {
        _ = Helpers.DispatcherHelper.RunOnUIThreadAsync(async () =>
        {
            try
            {
                await LoadMatchupHistoryAsync(message.MyChampion, message.EnemyLaner);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload matchup history on champ-select update");
            }
        });
    }

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync(PreGameChampInfo? champInfo)
    {
        try
        {
            // Check if tilt fix mode is enabled
            ShowMoodSelector = _configService.TiltFixEnabled;

            // Get last review focus
            var lastReview = await _gameRepo.GetLastReviewFocusAsync();
            if (lastReview != null)
            {
                LastFocus = lastReview.FocusNext;
                HasLastFocus = !string.IsNullOrWhiteSpace(lastReview.FocusNext);
                LastMistakes = lastReview.Mistakes;
                HasLastMistakes = !string.IsNullOrWhiteSpace(lastReview.Mistakes);

                // Pre-fill focus with last focus
                if (HasLastFocus)
                {
                    FocusText = lastReview.FocusNext;
                }
            }

            // Get active objective
            var objectives = await _objectivesRepo.GetActiveAsync();
            var relevantObjectives = objectives
                .Where(objective => ObjectivePhases.ShowsInPreGame(objective.Phase))
                .ToList();
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

                // Pre-fill focus with objective title if no prior focus
                if (!HasLastFocus && !string.IsNullOrWhiteSpace(ActiveObjectiveTitle))
                {
                    FocusText = ActiveObjectiveTitle;
                }
            }
            else
            {
                HasObjectiveFocusOptions = false;
                HasActiveObjective = false;
                ActiveObjectiveTitle = "";
                ActiveObjectiveCriteria = "";
            }

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
            ShowIntention = ShowMoodSelector && IsFirstGame;

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pre-game data");
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

    [RelayCommand]
    private void SelectMood(int mood)
    {
        SelectedMood = mood;
        IsTiltedSelected = mood == 1;
        IsOffSelected = mood == 2;
        IsNeutralSelected = mood == 3;
        IsGoodSelected = mood == 4;
        IsLockedInSelected = mood == 5;
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
}

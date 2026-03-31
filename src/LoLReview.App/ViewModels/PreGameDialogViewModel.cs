#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Services;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

public sealed class FocusObjectiveItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool IsPriority { get; init; }
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
}

/// <summary>ViewModel for the pre-game focus dialog shown during champion select.</summary>
public partial class PreGameDialogViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IConfigService _configService;
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

    // ── Constructor ─────────────────────────────────────────────────

    public PreGameDialogViewModel(
        IGameRepository gameRepo,
        IObjectivesRepository objectivesRepo,
        ISessionLogRepository sessionLogRepo,
        IConfigService configService,
        ILogger<PreGameDialogViewModel> logger)
    {
        _gameRepo = gameRepo;
        _objectivesRepo = objectivesRepo;
        _sessionLogRepo = sessionLogRepo;
        _configService = configService;
        _logger = logger;
    }

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
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
            var priorityObjective = await _objectivesRepo.GetPriorityAsync();
            var priorityObjectiveId = priorityObjective?.Id ?? 0L;
            ObjectiveFocusOptions.Clear();
            if (objectives.Count > 0)
            {
                foreach (var objective in objectives)
                {
                    ObjectiveFocusOptions.Add(new FocusObjectiveItem
                    {
                        Id = objective.Id,
                        Title = objective.Title,
                        Subtitle = string.IsNullOrWhiteSpace(objective.CompletionCriteria)
                            ? objective.SkillArea
                            : objective.CompletionCriteria,
                        IsPriority = objective.Id == priorityObjectiveId
                    });
                }

                HasObjectiveFocusOptions = ObjectiveFocusOptions.Count > 0;

                var obj = priorityObjective ?? objectives[0];
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pre-game data");
        }
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
}

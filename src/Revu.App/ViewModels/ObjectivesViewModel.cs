#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.App.Contracts;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.ViewModels;

public sealed record ObjectivePhaseUpdateRequest(long ObjectiveId, string Phase);

/// <summary>
/// v2.15.0 suggestion chip for the champion picker — a champ the user has
/// played that they can tap to add to the current objective's filter.
/// IsAdded reflects whether it's already in the picked list (for greyout).
/// </summary>
public sealed partial class ChampionSuggestion : ObservableObject
{
    public string Name { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotIsAdded))]
    [NotifyPropertyChangedFor(nameof(SuggestionOpacity))]
    private bool _isAdded;

    /// <summary>Inverse of <see cref="IsAdded"/> — XAML binding convenience.</summary>
    public bool NotIsAdded => !IsAdded;

    /// <summary>0.5 when already added (visually greyed), 1.0 when available to add.</summary>
    public double SuggestionOpacity => IsAdded ? 0.5 : 1.0;
}

/// <summary>
/// v2.15.0 draft row in the custom-prompt editor on the Objectives create/edit form.
/// A draft with OriginalId set maps to an existing DB row; null means it's new.
/// Diff-save in <see cref="ObjectivesViewModel.SavePromptsForObjectiveAsync"/>.
/// </summary>
public sealed partial class PromptDraftItem : ObservableObject
{
    public long? OriginalId { get; set; }

    [ObservableProperty]
    private string _phase = ObjectivePhases.InGame;

    [ObservableProperty]
    private string _label = "";

    /// <summary>UI binding: 0 = pre, 1 = in, 2 = post.</summary>
    public int PhaseIndex
    {
        get => ObjectivePhases.ToIndex(Phase);
        set
        {
            var newPhase = ObjectivePhases.FromIndex(value);
            if (!string.Equals(newPhase, Phase, StringComparison.OrdinalIgnoreCase))
            {
                Phase = newPhase;
                OnPropertyChanged();
            }
        }
    }

    partial void OnPhaseChanged(string value) => OnPropertyChanged(nameof(PhaseIndex));
}

/// <summary>Display model for an objective card.</summary>
public sealed class ObjectiveDisplayItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string SkillArea { get; init; } = "";
    public string Type { get; init; } = "primary";
    public string CompletionCriteria { get; init; } = "";
    public string Description { get; init; } = "";
    public string Phase { get; init; } = ObjectivePhases.InGame;
    public bool PracticePre { get; init; }
    public bool PracticeIn { get; init; }
    public bool PracticePost { get; init; }
    public int PromptCount { get; init; }
    public int Score { get; init; }
    public int GameCount { get; init; }
    public string Status { get; init; } = "active";
    public bool IsPriority { get; init; }

    // Level info
    public string LevelName { get; init; } = "Exploring";
    public int LevelIndex { get; init; }
    public double Progress { get; init; }
    public int? NextThreshold { get; init; }
    public bool CanComplete { get; init; }
    public bool SuggestComplete { get; init; }

    /// <summary>Color brush for the current level, distinct per progression stage.</summary>
    public SolidColorBrush LevelColorBrush => AppSemanticPalette.ObjectiveLevelBrush(LevelIndex);
    public SolidColorBrush TypeBadgeBackgroundBrush => AppSemanticPalette.TagSurfaceBrush(IsMental ? "neutral" : null, IsMental ? AppSemanticPalette.AccentTealHex : AppSemanticPalette.AccentBlueHex);
    public SolidColorBrush TypeBadgeForegroundBrush => AppSemanticPalette.TagAccentBrush(IsMental ? "neutral" : null, IsMental ? AppSemanticPalette.AccentTealHex : AppSemanticPalette.AccentBlueHex);
    public SolidColorBrush PriorityBadgeBackgroundBrush => AppSemanticPalette.Brush(AppSemanticPalette.AccentGoldDimHex);
    public SolidColorBrush PriorityBadgeForegroundBrush => AppSemanticPalette.Brush(AppSemanticPalette.AccentGoldHex);

    // Derived display properties
    public bool IsMental => Type == "mental";
    public string TypeBadge => IsMental ? "MENTAL" : "PRIMARY";
    public string PriorityBadge => IsPriority ? "PRIORITY" : "";
    public string ScoreText => $"{Score} pts  \u2022  {GameCount} games";
    public string ProgressText
    {
        get
        {
            if (NextThreshold.HasValue)
            {
                var needed = NextThreshold.Value - Score;
                return $"{needed} pts to next level";
            }
            return "Max level reached";
        }
    }
    public double ProgressPercent => Progress * 100;
    public bool HasSkillArea => !string.IsNullOrWhiteSpace(SkillArea);
    public bool HasCriteria => !string.IsNullOrWhiteSpace(CompletionCriteria);
    public int PhaseIndex => ObjectivePhases.ToIndex(Phase);
    public string PhaseLabel => ObjectivePhases.ToDisplayLabel(Phase);

    /// <summary>
    /// v2.15.0 compact multi-phase label — e.g. "PRE + IN", "ALL PHASES",
    /// "POST". Falls back to the legacy single-phase label if bools are all
    /// false (which shouldn't happen for a valid objective).
    /// </summary>
    public string PhasesSummary
    {
        get
        {
            if (PracticePre && PracticeIn && PracticePost) return "ALL PHASES";
            var parts = new System.Collections.Generic.List<string>();
            if (PracticePre)  parts.Add("PRE");
            if (PracticeIn)   parts.Add("IN");
            if (PracticePost) parts.Add("POST");
            return parts.Count == 0 ? PhaseLabel.ToUpperInvariant() : string.Join(" + ", parts);
        }
    }

    public bool HasPrompts => PromptCount > 0;
    public string PromptCountText =>
        PromptCount == 1 ? "1 CUSTOM PROMPT" : $"{PromptCount} CUSTOM PROMPTS";

    /// <summary>v2.15.0: champion-gating display. Empty = applies to all champions.</summary>
    public IReadOnlyList<string> Champions { get; init; } = [];
    public bool HasChampions => Champions.Count > 0;
    public string ChampionsSummary => HasChampions
        ? string.Join(", ", Champions).ToUpperInvariant()
        : "ALL CHAMPIONS";

    /// <summary>Cumulative score per game, oldest→newest, for sparkline rendering.</summary>
    public IReadOnlyList<int> ScoreHistory { get; init; } = [];
    public bool HasScoreHistory => ScoreHistory.Count >= 2;
    public string CriteriaText => $"Success: {CompletionCriteria}";
    public string RemainingText
    {
        get
        {
            if (CanComplete) return "";
            var needed = Math.Max(0, 50 - Score);
            return $"{needed} more pts to unlock completion (reach Ready level)";
        }
    }
}

/// <summary>Display model for a completed objective.</summary>
public sealed class CompletedObjectiveItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string PhaseLabel { get; init; } = "";
    public int Score { get; init; }
    public int GameCount { get; init; }
    public string SummaryText => $"{Score} pts  \u2022  {GameCount} games";
}

/// <summary>Recent spotted-problem note shown as backlog context for future objectives.</summary>
public sealed class SpottedProblemItem
{
    public long GameId { get; init; }
    public string ChampionName { get; init; } = "";
    public string EnemyChampion { get; init; } = "";
    public string DatePlayed { get; init; } = "";
    public string ProblemText { get; init; } = "";
    public bool Win { get; init; }
    public string ResultText => Win ? "W" : "L";

    /// <summary>"Kai'Sa vs Tristana" when enemy known, otherwise just "Kai'Sa".</summary>
    public string ChampionDisplay => string.IsNullOrWhiteSpace(EnemyChampion)
        ? ChampionName
        : $"{ChampionName} vs {EnemyChampion}";
}

/// <summary>ViewModel for the Objectives page.</summary>
public partial class ObjectivesViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IPromptsRepository _promptsRepo;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private long? _editingObjectiveId;

    [ObservableProperty]
    private bool _hasObjectives;

    [ObservableProperty]
    private bool _hasActiveObjectives;

    [ObservableProperty]
    private bool _hasCompletedObjectives;

    [ObservableProperty]
    private bool _showCompleted;

    [ObservableProperty]
    private bool _hasSpottedProblems;

    // Celebration overlay state
    [ObservableProperty]
    private bool _showCelebration;

    [ObservableProperty]
    private string _celebrationTitle = "";

    [ObservableProperty]
    private string _celebrationStats = "";

    // Create form fields
    [ObservableProperty]
    private string _newTitle = "";

    [ObservableProperty]
    private string _newSkillArea = "";

    [ObservableProperty]
    private int _newTypeIndex; // 0 = primary, 1 = mental

    [ObservableProperty]
    private int _newPhaseIndex = 1; // Legacy 0/1/2 index. Kept so nothing breaks mid-XAML-swap; new XAML binds to the three bools below.

    // v2.15.0: multi-phase practice bools replacing the single phase ComboBox.
    // At least one must be true for CanCreate to flip on (re-checked in RecomputeCanCreate).
    [ObservableProperty]
    private bool _newPracticePre;

    [ObservableProperty]
    private bool _newPracticeIn = true; // default to in-game — matches the old phase default

    [ObservableProperty]
    private bool _newPracticePost;

    [ObservableProperty]
    private string _newCriteria = "";

    [ObservableProperty]
    private string _newDescription = "";

    [ObservableProperty]
    private bool _canCreate;

    partial void OnNewPracticePreChanged(bool value)   => RecomputeCanCreate();
    partial void OnNewPracticeInChanged(bool value)    => RecomputeCanCreate();
    partial void OnNewPracticePostChanged(bool value)  => RecomputeCanCreate();

    private void RecomputeCanCreate()
    {
        CanCreate = !string.IsNullOrWhiteSpace(NewTitle)
                    && (NewPracticePre || NewPracticeIn || NewPracticePost);
    }

    public bool IsEditingObjective => EditingObjectiveId.HasValue;
    public string ObjectiveFormTitle => IsEditingObjective ? "Edit Objective" : "New Objective";
    public string SaveObjectiveButtonText => IsEditingObjective ? "Save Changes" : "Create";

    public ObservableCollection<ObjectiveDisplayItem> ActiveObjectives { get; } = new();
    public ObservableCollection<CompletedObjectiveItem> CompletedObjectives { get; } = new();
    public ObservableCollection<SpottedProblemItem> SpottedProblems { get; } = new();

    // v2.15.0: custom prompt editor attached to the create/edit form. Each
    // row is a draft; existing rows carry their OriginalId so diff-save can
    // tell which to UPDATE vs DELETE vs INSERT. Rows inherit sort order from
    // their position in this collection.
    public ObservableCollection<PromptDraftItem> NewPrompts { get; } = new();

    // v2.15.0: champion gating. When this list is non-empty, the objective
    // only surfaces in pre/post-game when the current champion matches one.
    // Empty = applies to all champions.
    public ObservableCollection<string> NewChampions { get; } = new();

    /// <summary>Suggested champions derived from the user's played history.</summary>
    public ObservableCollection<ChampionSuggestion> ChampionSuggestions { get; } = new();

    /// <summary>
    /// v2.15.1: flat list of champion names for the AutoSuggestBox search
    /// dropdown. Populated from GetPlayedChampionsAsync at load time.
    /// </summary>
    public IReadOnlyList<string> AllChampionNames { get; private set; } = Array.Empty<string>();

    [ObservableProperty]
    private string _newChampionInput = "";

    public bool HasNewChampions => NewChampions.Count > 0;

    public ObjectivesViewModel(
        IGameRepository gameRepo,
        IObjectivesRepository objectivesRepo,
        IPromptsRepository promptsRepo,
        INavigationService navigationService)
    {
        _gameRepo = gameRepo;
        _objectivesRepo = objectivesRepo;
        _promptsRepo = promptsRepo;
        _navigationService = navigationService;
    }

    partial void OnNewTitleChanged(string value)
    {
        RecomputeCanCreate();
    }

    partial void OnEditingObjectiveIdChanged(long? value)
    {
        OnPropertyChanged(nameof(IsEditingObjective));
        OnPropertyChanged(nameof(ObjectiveFormTitle));
        OnPropertyChanged(nameof(SaveObjectiveButtonText));
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await RefreshDataAsync();
            await LoadChampionSuggestionsAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadChampionSuggestionsAsync()
    {
        try
        {
            // v2.15.1: pull a wider set (up to 200) for the AutoSuggestBox search.
            // The flat list drives the typeahead dropdown; the ChampionSuggestions
            // collection is legacy but kept for any remaining bindings.
            var played = await _objectivesRepo.GetPlayedChampionsAsync(200);
            AllChampionNames = played.ToList();
            ChampionSuggestions.Clear();
            foreach (var name in played.Take(30))
            {
                ChampionSuggestions.Add(new ChampionSuggestion
                {
                    Name = name,
                    IsAdded = NewChampions.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)),
                });
            }
        }
        catch
        {
            // Non-fatal — picker falls back to manual-entry-only.
        }
    }

    [RelayCommand]
    private void ToggleCreateForm()
    {
        IsCreating = !IsCreating;
        if (!IsCreating)
        {
            ClearForm();
            return;
        }

        EditingObjectiveId = null;
        ResetFormFields();
    }

    [RelayCommand]
    private async Task CreateObjectiveAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTitle)) return;
        if (!NewPracticePre && !NewPracticeIn && !NewPracticePost)
        {
            // UI should have prevented this via CanCreate, but double-check.
            return;
        }

        var type = NewTypeIndex == 1 ? "mental" : "primary";
        long objectiveId;
        if (EditingObjectiveId.HasValue)
        {
            objectiveId = EditingObjectiveId.Value;
            await _objectivesRepo.UpdateWithPhasesAsync(
                objectiveId,
                NewTitle.Trim(),
                NewSkillArea.Trim(),
                type,
                NewCriteria.Trim(),
                NewDescription.Trim(),
                NewPracticePre, NewPracticeIn, NewPracticePost);
        }
        else
        {
            objectiveId = await _objectivesRepo.CreateWithPhasesAsync(
                NewTitle.Trim(),
                NewSkillArea.Trim(),
                type,
                NewCriteria.Trim(),
                NewDescription.Trim(),
                NewPracticePre, NewPracticeIn, NewPracticePost);
        }

        await SavePromptsForObjectiveAsync(objectiveId);

        // v2.15.0 champion gating: persist picked champions (empty = all champs).
        await _objectivesRepo.SetChampionsForObjectiveAsync(objectiveId, NewChampions.ToList());

        ClearForm();
        IsCreating = false;
        await RefreshDataAsync();
    }

    /// <summary>
    /// Diff-save the current NewPrompts draft list against the stored prompts
    /// for this objective: insert new rows, update changed ones, delete removed.
    /// Sort order is the draft's index in NewPrompts.
    /// </summary>
    private async Task SavePromptsForObjectiveAsync(long objectiveId)
    {
        var existing = await _promptsRepo.GetPromptsForObjectiveAsync(objectiveId);
        var existingById = existing.ToDictionary(p => p.Id);

        var keptIds = new HashSet<long>();

        for (int i = 0; i < NewPrompts.Count; i++)
        {
            var draft = NewPrompts[i];
            var label = (draft.Label ?? "").Trim();
            if (string.IsNullOrEmpty(label)) continue; // blank rows don't persist

            var phase = ObjectivePhases.Normalize(draft.Phase);

            if (draft.OriginalId.HasValue && existingById.TryGetValue(draft.OriginalId.Value, out var prior))
            {
                keptIds.Add(prior.Id);
                // Only write if something changed — avoid spurious updated_at churn.
                if (prior.Phase != phase || prior.Label != label || prior.SortOrder != i)
                {
                    await _promptsRepo.UpdatePromptAsync(prior.Id, phase, label, i);
                }
            }
            else
            {
                var newId = await _promptsRepo.CreatePromptAsync(objectiveId, phase, label, i);
                keptIds.Add(newId);
                draft.OriginalId = newId; // stamp so a subsequent edit-save doesn't re-insert
            }
        }

        // Anything that was on disk but isn't in the draft list anymore was deleted.
        foreach (var prior in existing)
        {
            if (!keptIds.Contains(prior.Id))
            {
                await _promptsRepo.DeletePromptAsync(prior.Id);
            }
        }
    }

    [RelayCommand]
    private void AddChampion(string? championName)
    {
        var name = (championName ?? NewChampionInput ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        // De-dupe case-insensitively but preserve user's casing on insert.
        if (NewChampions.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) return;

        NewChampions.Add(name);
        NewChampionInput = "";
        OnPropertyChanged(nameof(HasNewChampions));
        // Suggestion chip should gray out — reflect already-added state.
        foreach (var s in ChampionSuggestions)
        {
            s.IsAdded = NewChampions.Any(c => string.Equals(c, s.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [RelayCommand]
    private void RemoveChampion(string? championName)
    {
        if (string.IsNullOrWhiteSpace(championName)) return;
        var existing = NewChampions.FirstOrDefault(c => string.Equals(c, championName, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        NewChampions.Remove(existing);
        OnPropertyChanged(nameof(HasNewChampions));
        foreach (var s in ChampionSuggestions)
        {
            s.IsAdded = NewChampions.Any(c => string.Equals(c, s.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [RelayCommand]
    private void AddPrompt()
    {
        NewPrompts.Add(new PromptDraftItem
        {
            // Default new rows to the first checked phase so they're immediately actionable.
            Phase = NewPracticePre ? ObjectivePhases.PreGame
                  : NewPracticeIn  ? ObjectivePhases.InGame
                  : NewPracticePost ? ObjectivePhases.PostGame
                  : ObjectivePhases.InGame,
            Label = "",
            OriginalId = null,
        });
    }

    [RelayCommand]
    private void RemovePrompt(PromptDraftItem? draft)
    {
        if (draft is null) return;
        NewPrompts.Remove(draft);
    }

    [RelayCommand]
    private async Task MarkCompleteAsync(long objectiveId)
    {
        var objective = ActiveObjectives.FirstOrDefault(o => o.Id == objectiveId);

        await _objectivesRepo.MarkCompleteAsync(objectiveId);
        await RefreshDataAsync();

        if (objective is not null)
        {
            CelebrationTitle = objective.Title;
            CelebrationStats = $"{objective.Score} pts  \u2022  {objective.GameCount} games played";
            ShowCelebration = true;
            _ = AutoDismissCelebrationAsync();
        }
    }

    [RelayCommand]
    private void DismissCelebration()
    {
        ShowCelebration = false;
    }

    private async Task AutoDismissCelebrationAsync()
    {
        await Task.Delay(5000);
        ShowCelebration = false;
    }

    [RelayCommand]
    private async Task SetPriorityAsync(long objectiveId)
    {
        await _objectivesRepo.SetPriorityAsync(objectiveId);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task DeleteObjectiveAsync(long objectiveId)
    {
        await _objectivesRepo.DeleteAsync(objectiveId);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task UpdateObjectivePhaseAsync(ObjectivePhaseUpdateRequest request)
    {
        await _objectivesRepo.UpdatePhaseAsync(request.ObjectiveId, request.Phase);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task BeginEditObjectiveAsync(long objectiveId)
    {
        var objective = await _objectivesRepo.GetAsync(objectiveId);
        if (objective is null)
        {
            return;
        }

        EditingObjectiveId = objective.Id;
        NewTitle = objective.Title;
        NewSkillArea = objective.SkillArea;
        NewTypeIndex = string.Equals(objective.Type, "mental", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        NewPhaseIndex = ObjectivePhases.ToIndex(objective.Phase);
        NewPracticePre  = objective.PracticePre;
        NewPracticeIn   = objective.PracticeIn;
        NewPracticePost = objective.PracticePost;
        NewCriteria = objective.CompletionCriteria;
        NewDescription = objective.Description;

        // Hydrate the custom prompt editor from DB.
        NewPrompts.Clear();
        var existingPrompts = await _promptsRepo.GetPromptsForObjectiveAsync(objectiveId);
        foreach (var p in existingPrompts)
        {
            NewPrompts.Add(new PromptDraftItem
            {
                OriginalId = p.Id,
                Phase = p.Phase,
                Label = p.Label,
            });
        }

        // v2.15.0: hydrate champion filter from DB.
        NewChampions.Clear();
        var existingChamps = await _objectivesRepo.GetChampionsForObjectiveAsync(objectiveId);
        foreach (var c in existingChamps) NewChampions.Add(c);
        OnPropertyChanged(nameof(HasNewChampions));

        await LoadChampionSuggestionsAsync();

        RecomputeCanCreate();
        IsCreating = true;
    }

    [RelayCommand]
    private void ToggleCompleted()
    {
        ShowCompleted = !ShowCompleted;
    }

    [RelayCommand]
    private void ViewGames(long objectiveId)
    {
        _navigationService.NavigateTo("objectivegames", objectiveId);
    }

    [RelayCommand]
    private void ViewNotes(long objectiveId)
    {
        _navigationService.NavigateTo("objectivenotes", objectiveId);
    }

    private void ClearForm()
    {
        EditingObjectiveId = null;
        ResetFormFields();
    }

    private void ResetFormFields()
    {
        NewTitle = "";
        NewSkillArea = "";
        NewTypeIndex = 0;
        NewPhaseIndex = 1;
        NewPracticePre = false;
        NewPracticeIn = true;
        NewPracticePost = false;
        NewCriteria = "";
        NewDescription = "";
        NewPrompts.Clear();
        NewChampions.Clear();
        NewChampionInput = "";
        OnPropertyChanged(nameof(HasNewChampions));
        foreach (var s in ChampionSuggestions) s.IsAdded = false;
        RecomputeCanCreate();
    }

    private async Task RefreshDataAsync()
    {
        var allObjectives = await _objectivesRepo.GetAllAsync();
        var spottedProblems = await _gameRepo.GetRecentSpottedProblemsAsync(limit: 12);

        ActiveObjectives.Clear();
        CompletedObjectives.Clear();
        SpottedProblems.Clear();

        foreach (var obj in allObjectives)
        {
            if (string.Equals(obj.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                var levelInfo = IObjectivesRepository.GetLevelInfo(obj.Score, obj.GameCount);
                var scoreHistory = await _objectivesRepo.GetScoreHistoryAsync(obj.Id);
                var prompts = await _promptsRepo.GetPromptsForObjectiveAsync(obj.Id);
                var champions = await _objectivesRepo.GetChampionsForObjectiveAsync(obj.Id);

                ActiveObjectives.Add(new ObjectiveDisplayItem
                {
                    Id = obj.Id,
                    Title = obj.Title,
                    SkillArea = obj.SkillArea,
                    Type = obj.Type,
                    CompletionCriteria = obj.CompletionCriteria,
                    Description = obj.Description,
                    Phase = obj.Phase,
                    PracticePre = obj.PracticePre,
                    PracticeIn = obj.PracticeIn,
                    PracticePost = obj.PracticePost,
                    PromptCount = prompts.Count,
                    Champions = champions,
                    Score = obj.Score,
                    GameCount = obj.GameCount,
                    Status = obj.Status,
                    IsPriority = obj.IsPriority,
                    LevelName = levelInfo.LevelName,
                    LevelIndex = levelInfo.LevelIndex,
                    Progress = levelInfo.Progress,
                    NextThreshold = levelInfo.NextThreshold,
                    CanComplete = levelInfo.CanComplete,
                    SuggestComplete = levelInfo.SuggestComplete,
                    ScoreHistory = scoreHistory,
                });
            }
            else
            {
                CompletedObjectives.Add(new CompletedObjectiveItem
                {
                    Id = obj.Id,
                    Title = obj.Title,
                    PhaseLabel = ObjectivePhases.ToDisplayLabel(obj.Phase),
                    Score = obj.Score,
                    GameCount = obj.GameCount,
                });
            }
        }

        HasActiveObjectives = ActiveObjectives.Count > 0;
        HasCompletedObjectives = CompletedObjectives.Count > 0;
        foreach (var problem in spottedProblems)
        {
            SpottedProblems.Add(new SpottedProblemItem
            {
                GameId = problem.GameId,
                ChampionName = problem.ChampionName,
                EnemyChampion = problem.EnemyChampion,
                DatePlayed = problem.DatePlayed,
                ProblemText = problem.SpottedProblems,
                Win = problem.Win
            });
        }

        HasSpottedProblems = SpottedProblems.Count > 0;
        HasObjectives = HasActiveObjectives || HasCompletedObjectives;
    }
}

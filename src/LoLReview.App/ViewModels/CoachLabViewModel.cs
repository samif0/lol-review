#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LoLReview.App.ViewModels;

public sealed class CoachOptionItem
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class CoachMomentItem : ObservableObject
{
    public long Id { get; init; }
    public long GameId { get; init; }
    public string SourceType { get; init; } = "";
    public string Champion { get; init; } = "";
    public string Role { get; init; } = "";
    public int GameTimeS { get; init; }
    public string ClipPath { get; init; } = "";
    public string StoryboardPath { get; init; } = "";
    public string HudStripPath { get; init; } = "";
    public string MinimapStripPath { get; init; } = "";
    public string NoteText { get; init; } = "";
    public string ContextText { get; init; } = "";
    public string DraftQuality { get; init; } = "";
    public string DraftPrimaryReason { get; init; } = "";
    public string DraftObjectiveKey { get; init; } = "";
    public long? DraftAttachedObjectiveId { get; init; }
    public string DraftAttachedObjectiveTitle { get; init; } = "";
    public double DraftConfidence { get; init; }
    public string DraftRationale { get; init; } = "";
    public string LabelQuality { get; init; } = "";
    public string LabelPrimaryReason { get; init; } = "";
    public string LabelObjectiveKey { get; init; } = "";
    public long? LabelAttachedObjectiveId { get; init; }
    public string LabelAttachedObjectiveTitle { get; init; } = "";
    public long? BlockObjectiveId { get; init; }
    public string BlockObjectiveTitle { get; init; } = "";
    public string LabelExplanation { get; init; } = "";
    public double LabelConfidence { get; init; }

    public bool HasManualLabel => !string.IsNullOrWhiteSpace(LabelQuality);
    public string SourceBadge => SourceType == "manual_clip" ? "Manual Clip" : "Auto Sample";
    public string TimeText => $"{GameTimeS / 60}:{GameTimeS % 60:D2}";
    public string DraftSummary => $"{DraftQuality} | {DisplayOrFallback(DraftPrimaryReason)} | {(int)Math.Round(DraftConfidence * 100)}%";
    public string LabelSummary => HasManualLabel
        ? BuildManualLabelSummary(LabelQuality, LabelPrimaryReason, AttachedObjectiveTitle, LabelConfidence)
        : "No manual label yet";
    public long? AttachedObjectiveId => LabelAttachedObjectiveId ?? DraftAttachedObjectiveId;
    public string AttachedObjectiveTitle =>
        !string.IsNullOrWhiteSpace(LabelAttachedObjectiveTitle) ? LabelAttachedObjectiveTitle :
        DraftAttachedObjectiveTitle;
    public string AttachedObjectiveSource =>
        !string.IsNullOrWhiteSpace(LabelAttachedObjectiveTitle) || LabelAttachedObjectiveId.HasValue ? "manual" :
        !string.IsNullOrWhiteSpace(DraftAttachedObjectiveTitle) || DraftAttachedObjectiveId.HasValue ? "model" :
        "";
    public bool HasAttachedObjective => AttachedObjectiveId.HasValue || !string.IsNullOrWhiteSpace(AttachedObjectiveTitle);
    public string QueueObjectiveBadgeText => HasAttachedObjective
        ? AttachedObjectiveTitle
        : "Unattached";

    private static string DisplayOrFallback(string value) =>
        string.IsNullOrWhiteSpace(value) ? "needs review" : value.Replace('_', ' ');

    private static string BuildManualLabelSummary(string quality, string primaryReason, string attachedObjectiveTitle, double confidence)
    {
        var parts = new List<string>
        {
            quality
        };

        var reason = SummarizeReason(primaryReason);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            parts.Add(reason);
        }

        if (!string.IsNullOrWhiteSpace(attachedObjectiveTitle))
        {
            parts.Add(attachedObjectiveTitle);
        }

        parts.Add($"{(int)Math.Round(confidence * 100)}%");
        return string.Join(" | ", parts);
    }

    private static string SummarizeReason(string value)
    {
        var cleaned = (value ?? string.Empty).Trim().Replace('_', ' ');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "";
        }

        return cleaned.Length <= 72
            ? cleaned
            : $"{cleaned[..69]}...";
    }
}

public partial class CoachLabViewModel : ObservableObject
{
    private const int MomentQueueFetchLimit = 250;
    private const int MomentPageSize = 12;

    private readonly ICoachLabService _coachLabService;
    private readonly ICoachTrainingService _coachTrainingService;
    private readonly IObjectivesRepository _objectivesRepository;
    private readonly INavigationService _navigationService;
    private readonly ILogger<CoachLabViewModel> _logger;
    private readonly List<CoachMomentItem> _allMoments = [];
    private readonly List<CoachMomentItem> _filteredMoments = [];
    private Task? _trainingMonitorTask;
    private long? _lastSeenTrainingCompletedAt;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isTrainingInProgress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _activeObjectiveTitle = "Observe lane phase";
    [ObservableProperty] private string _modeText = "Model setup required";
    [ObservableProperty] private string _recommendationTitle = "Model setup required";
    [ObservableProperty] private string _recommendationSummary = "";
    [ObservableProperty] private string _watchItemTitle = "Clip-backed evidence";
    [ObservableProperty] private string _watchItemSummary = "";
    [ObservableProperty] private int _totalMoments;
    [ObservableProperty] private int _reviewedMoments;
    [ObservableProperty] private int _pendingMoments;
    [ObservableProperty] private string _trainingSummary = "";
    [ObservableProperty] private bool _hasGemmaModel;
    [ObservableProperty] private string _modelOutputTitle = "Coach Read";
    [ObservableProperty] private string _modelOutputSummary = "Register or train the coach model, then use it to summarize recurring problems or suggest the next objective.";
    [ObservableProperty] private CoachMomentItem? _selectedMoment;
    [ObservableProperty] private string _selectedLabelQuality = "neutral";
    [ObservableProperty] private string _selectedAttachedObjectiveId = "";
    [ObservableProperty] private string _selectedExplanation = "";
    [ObservableProperty] private double _selectedConfidence = 0.7;
    [ObservableProperty] private BitmapImage? _selectedStoryboardImage;
    [ObservableProperty] private BitmapImage? _selectedMinimapImage;
    [ObservableProperty] private string _selectedQueueObjectiveKey = "__all__";
    [ObservableProperty] private string _selectedQueueManualLabelKey = "__all__";
    [ObservableProperty] private bool _canCreateSuggestedObjective;
    [ObservableProperty] private int _currentMomentPage = 1;
    [ObservableProperty] private int _totalMomentPages = 1;
    [ObservableProperty] private string _momentPageSummary = "0 moments";

    public ObservableCollection<CoachMomentItem> Moments { get; } = new();
    public ObservableCollection<CoachOptionItem> QualityOptions { get; } =
    [
        new() { Key = "bad", Label = "Bad" },
        new() { Key = "neutral", Label = "Neutral" },
        new() { Key = "good", Label = "Good" },
    ];
    public ObservableCollection<CoachOptionItem> ObjectiveOptions { get; } = new();
    public ObservableCollection<CoachOptionItem> QueueObjectiveOptions { get; } = new();
    public ObservableCollection<CoachOptionItem> QueueManualLabelOptions { get; } =
    [
        new() { Key = "__all__", Label = "All Moments" },
        new() { Key = "__none__", Label = "Needs Review" },
        new() { Key = "__has__", Label = "Reviewed" },
    ];

    public bool HasSelection => SelectedMoment is not null;
    public bool IsWorking => IsBusy || IsTrainingInProgress;
    public string SelectedMomentChampion => SelectedMoment?.Champion ?? "";
    public string SelectedMomentDraftRationale => SelectedMoment?.DraftRationale ?? "";
    public string SelectedMomentNoteText => SelectedMoment?.NoteText ?? "";
    public string SelectedMomentContextText => SelectedMoment?.ContextText ?? "";
    public string SelectedMomentTimeText => SelectedMoment?.TimeText ?? "";
    public string SelectedMomentSourceText => SelectedMoment?.SourceBadge ?? "";
    public string SelectedMomentDraftSummary => SelectedMoment?.DraftSummary ?? "No draft yet";
    public string SelectedMomentLabelSummary => SelectedMoment is null
        ? ""
        : SelectedMoment.HasManualLabel
            ? SelectedMoment.LabelSummary
            : "Not reviewed yet";
    public string SelectedMomentProgressText => BuildSelectionProgressText();
    public string ReviewActionTitle => SelectedMoment?.HasManualLabel == true ? "Update review" : "Review clip";
    public string SelectedConfidenceText => $"Confidence: {SelectedConfidence:F2}";
    public string SuggestedObjectiveButtonText => "Add Suggested Objective";
    public bool CanGoToPreviousMoment => GetSelectedFilteredIndex() > 0;
    public bool CanGoToNextMoment => GetSelectedFilteredIndex() is var index && index >= 0 && index < _filteredMoments.Count - 1;
    public bool CanGoToPreviousMomentPage => CurrentMomentPage > 1;
    public bool CanGoToNextMomentPage => CurrentMomentPage < TotalMomentPages;
    public bool HasMultipleMomentPages => TotalMomentPages > 1;

    private CoachObjectiveSuggestion? _latestSuggestion;

    public CoachLabViewModel(
        ICoachLabService coachLabService,
        ICoachTrainingService coachTrainingService,
        IObjectivesRepository objectivesRepository,
        INavigationService navigationService,
        ILogger<CoachLabViewModel> logger)
    {
        _coachLabService = coachLabService;
        _coachTrainingService = coachTrainingService;
        _objectivesRepository = objectivesRepository;
        _navigationService = navigationService;
        _logger = logger;
        IsEnabled = coachLabService.IsEnabled;
    }

    partial void OnSelectedMomentChanged(CoachMomentItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedMomentChampion));
        OnPropertyChanged(nameof(SelectedMomentDraftRationale));
        OnPropertyChanged(nameof(SelectedMomentNoteText));
        OnPropertyChanged(nameof(SelectedMomentContextText));
        OnPropertyChanged(nameof(SelectedMomentTimeText));
        OnPropertyChanged(nameof(SelectedMomentSourceText));
        OnPropertyChanged(nameof(SelectedMomentDraftSummary));
        OnPropertyChanged(nameof(SelectedMomentLabelSummary));
        OnPropertyChanged(nameof(SelectedMomentProgressText));
        OnPropertyChanged(nameof(ReviewActionTitle));

        if (value is null)
        {
            SelectedLabelQuality = "neutral";
            SelectedAttachedObjectiveId = "";
            SelectedExplanation = "";
            SelectedConfidence = 0.7;
            SelectedStoryboardImage = null;
            SelectedMinimapImage = null;
            SelectPreviousMomentCommand.NotifyCanExecuteChanged();
            SelectNextMomentCommand.NotifyCanExecuteChanged();
            return;
        }

        SelectedLabelQuality = value.HasManualLabel ? value.LabelQuality : value.DraftQuality;
        SelectedAttachedObjectiveId = value.AttachedObjectiveId?.ToString() ?? "";
        SelectedExplanation = value.HasManualLabel ? value.LabelExplanation : "";
        SelectedConfidence = value.HasManualLabel
            ? Math.Clamp(value.LabelConfidence, 0.1, 1.0)
            : Math.Clamp(value.DraftConfidence, 0.2, 1.0);
        SelectedStoryboardImage = LoadBitmap(value.StoryboardPath);
        SelectedMinimapImage = LoadBitmap(value.MinimapStripPath);
        SelectPreviousMomentCommand.NotifyCanExecuteChanged();
        SelectNextMomentCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedConfidenceChanged(double value)
    {
        OnPropertyChanged(nameof(SelectedConfidenceText));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorking));
    }

    partial void OnIsTrainingInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorking));
    }

    partial void OnSelectedQueueObjectiveKeyChanged(string value)
    {
        ApplyMomentFilter(resetPage: true);
    }

    partial void OnSelectedQueueManualLabelKeyChanged(string value)
    {
        ApplyMomentFilter(resetPage: true);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsEnabled = _coachLabService.IsEnabled;
        if (!IsEnabled)
        {
            StatusText = "Coach Lab is hidden on this install.";
            Moments.Clear();
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Loading coach lab...";
            await RefreshAsync();
            EnsureTrainingMonitor();
            if (!IsTrainingInProgress)
            {
                StatusText = Moments.Count == 0
                    ? "No coach moments yet. Save a few VOD clips with notes, then sync again."
                    : "Coach Lab ready.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load coach lab");
            StatusText = $"Coach Lab failed to load: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncAsync()
    {
        if (!IsEnabled) return;

        IsBusy = true;
        try
        {
            StatusText = "Syncing manual clips...";
            var result = await _coachLabService.SyncMomentsAsync(includeAutoSamples: false);
            await RefreshAsync();
            StatusText = result.ReviewNoteLabelsApplied > 0
                ? $"Imported {result.ManualClipsImported} clip(s), applied {result.ReviewNoteLabelsApplied} automatic final tag(s), and created {result.DraftsCreated} draft read(s)."
                : $"Imported {result.ManualClipsImported} clip(s) and created {result.DraftsCreated} draft read(s).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coach sync failed");
            StatusText = $"Coach sync failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshRecommendationAsync()
    {
        if (!IsEnabled) return;

        IsBusy = true;
        try
        {
            StatusText = "Refreshing the latest coach read...";
            await _coachLabService.RefreshRecommendationAsync();
            await RefreshAsync();
            StatusText = "Coach read refreshed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coach recommendation refresh failed");
            StatusText = $"Coach read refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TrainAsync()
    {
        if (!IsEnabled) return;

        IsBusy = true;
        try
        {
            StatusText = "Registering or training the coach model from prepared clips...";
            var result = await _coachTrainingService.TrainGemmaModelAsync();
            await RefreshAsync();
            EnsureTrainingMonitor();
            ModelOutputTitle = result.AlreadyRunning ? "Training already running" : "Coach training";
            ModelOutputSummary = ToUiCopy(result.Summary);
            StatusText = ToUiCopy(result.Summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coach training failed");
            StatusText = $"Coach training failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveLabelAsync()
    {
        if (!IsEnabled || SelectedMoment is null)
        {
            StatusText = SelectedMoment is null
                ? "No moment selected — click a clip first."
                : "Coach Lab is not enabled.";
            return;
        }

        var momentId = SelectedMoment?.Id;
        await SaveReviewInternalAsync(momentId, momentId is null ? "Review saved." : $"Review saved on clip {momentId}.");
    }

    [RelayCommand]
    private async Task SaveAndNextAsync()
    {
        var nextMomentId = GetAdjacentMomentId(1);
        await SaveReviewInternalAsync(
            nextMomentId ?? SelectedMoment?.Id,
            nextMomentId.HasValue ? "Review saved. Moved to the next clip." : "Review saved.");
    }

    [RelayCommand]
    private async Task FindProblemsAsync()
    {
        if (!IsEnabled) return;

        IsBusy = true;
        try
        {
            StatusText = "Reading recurring problems from the saved clips...";
            var report = await _coachLabService.GetModelProblemsAsync();
            _latestSuggestion = null;
            CanCreateSuggestedObjective = false;
            ModelOutputTitle = ToUiCopy(report.Title);
            ModelOutputSummary = ToUiCopy(report.Summary);
            StatusText = report.UsesTrainedModel
                ? "Problem read generated."
                : ToUiCopy(report.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coach problem query failed");
            StatusText = $"Problem read failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SuggestObjectiveAsync()
    {
        if (!IsEnabled) return;

        IsBusy = true;
        try
        {
            StatusText = "Reading whether you should keep or change objective focus...";
            var suggestion = await _coachLabService.GenerateObjectiveSuggestionAsync();
            _latestSuggestion = suggestion;
            CanCreateSuggestedObjective = suggestion.CanCreateObjective;
            ModelOutputTitle = ToUiCopy(suggestion.Title);
            ModelOutputSummary = ToUiCopy(suggestion.Summary);

            if (suggestion.AttachedObjectiveId.HasValue)
            {
                var objectiveKey = suggestion.AttachedObjectiveId.Value.ToString();
                if (QueueObjectiveOptions.Any(option => option.Key == objectiveKey))
                {
                    SelectedQueueObjectiveKey = objectiveKey;
                }
            }

            StatusText = suggestion.UsesTrainedModel
                ? "Objective read generated."
                : ToUiCopy(suggestion.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model objective suggestion failed");
            StatusText = $"Model objective suggestion failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddSuggestedObjectiveAsync()
    {
        if (_latestSuggestion is null || !_latestSuggestion.CanCreateObjective)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var objectiveId = await _objectivesRepository.CreateAsync(
                title: _latestSuggestion.CandidateObjectiveTitle,
                skillArea: "Coach Lab",
                type: "primary",
                completionCriteria: _latestSuggestion.CandidateCompletionCriteria,
                description: _latestSuggestion.CandidateDescription,
                phase: ObjectivePhases.InGame);

            await RefreshAsync(SelectedMoment?.Id);
            SelectedAttachedObjectiveId = objectiveId.ToString();
            CanCreateSuggestedObjective = false;
            StatusText = $"Added \"{_latestSuggestion.CandidateObjectiveTitle}\" as a new objective.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adding suggested objective failed");
            StatusText = $"Adding suggested objective failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenSelectedClipAsync()
    {
        if (SelectedMoment is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            _navigationService.NavigateTo("vodplayer", new VodPlayerNavigationRequest
            {
                GameId = SelectedMoment.GameId,
                SeekTimeS = SelectedMoment.GameTimeS
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open clip in VOD viewer for game {GameId}", SelectedMoment.GameId);
            StatusText = $"Failed to open clip in VOD viewer: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task PlayClipFileAsync()
    {
        var clipPath = SelectedMoment?.ClipPath;
        if (string.IsNullOrWhiteSpace(clipPath))
        {
            StatusText = "No clip file associated with this moment.";
            return Task.CompletedTask;
        }

        if (!File.Exists(clipPath))
        {
            StatusText = "Clip file no longer exists on disk.";
            return Task.CompletedTask;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = clipPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to play clip file {Path}", clipPath);
            StatusText = $"Failed to play clip: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task OpenStoryboardAsync() => OpenImagePathAsync(
        SelectedMoment?.StoryboardPath,
        "Storyboard image no longer exists on disk.");

    [RelayCommand]
    private Task OpenMinimapAsync() => OpenImagePathAsync(
        SelectedMoment?.MinimapStripPath,
        "Minimap strip no longer exists on disk.");

    [RelayCommand]
    private void OpenSelectedVod()
    {
        if (SelectedMoment is null)
        {
            return;
        }

        _navigationService.NavigateTo("vodplayer", SelectedMoment.GameId);
    }

    private async Task RefreshAsync(long? preserveMomentId = null)
    {
        var dashboard = await _coachLabService.GetDashboardAsync();
        var queue = await _coachLabService.GetMomentQueueAsync(MomentQueueFetchLimit);
        var objectiveOptions = await LoadObjectiveOptionsAsync();

        ActiveObjectiveTitle = dashboard.ActiveObjectiveTitle;
        ModeText = dashboard.TrainingStatus.HasGemmaAdapter
            ? "Adapter active"
            : dashboard.TrainingStatus.HasGemmaBaseModel
                ? "Base model active"
                : "Model setup required";
        RecommendationTitle = ToUiCopy(dashboard.RecommendationTitle);
        RecommendationSummary = ToUiCopy(dashboard.RecommendationSummary);
        WatchItemTitle = dashboard.WatchItemTitle;
        WatchItemSummary = ToUiCopy(dashboard.WatchItemSummary);
        TotalMoments = dashboard.TotalMoments;
        ReviewedMoments = dashboard.GoldMoments;
        PendingMoments = dashboard.PendingMoments;
        TrainingSummary = ToUiCopy(dashboard.TrainingStatus.Summary);
        HasGemmaModel = dashboard.TrainingStatus.HasGemmaBaseModel
            || dashboard.TrainingStatus.HasGemmaAdapter;
        ApplyTrainingStatus(dashboard.TrainingStatus);
        RebuildObjectiveCollections(objectiveOptions);

        if (!HasGemmaModel)
        {
            ModelOutputTitle = "Coach read";
            ModelOutputSummary = "Register or train the coach model, then use it to summarize recurring problems or suggest the next objective.";
            CanCreateSuggestedObjective = false;
            _latestSuggestion = null;
        }

        _allMoments.Clear();
        foreach (var moment in queue)
        {
            _allMoments.Add(new CoachMomentItem
            {
                Id = moment.Id,
                GameId = moment.GameId,
                SourceType = moment.SourceType,
                Champion = moment.Champion,
                Role = moment.Role,
                GameTimeS = moment.GameTimeS,
                ClipPath = moment.ClipPath,
                StoryboardPath = moment.StoryboardPath,
                HudStripPath = moment.HudStripPath,
                MinimapStripPath = moment.MinimapStripPath,
                NoteText = moment.NoteText,
                ContextText = moment.ContextText,
                DraftQuality = moment.DraftQuality,
                DraftPrimaryReason = moment.DraftPrimaryReason,
                DraftObjectiveKey = moment.DraftObjectiveKey,
                DraftAttachedObjectiveId = moment.DraftAttachedObjectiveId,
                DraftAttachedObjectiveTitle = moment.DraftAttachedObjectiveTitle,
                DraftConfidence = moment.DraftConfidence,
                DraftRationale = moment.DraftRationale,
                LabelQuality = moment.LabelQuality,
                LabelPrimaryReason = moment.LabelPrimaryReason,
                LabelObjectiveKey = moment.LabelObjectiveKey,
                LabelAttachedObjectiveId = moment.LabelAttachedObjectiveId,
                LabelAttachedObjectiveTitle = moment.LabelAttachedObjectiveTitle,
                BlockObjectiveId = moment.BlockObjectiveId,
                BlockObjectiveTitle = moment.BlockObjectiveTitle,
                LabelExplanation = moment.LabelExplanation,
                LabelConfidence = moment.LabelConfidence,
            });
        }

        ApplyMomentFilter(preserveMomentId, resetPage: !preserveMomentId.HasValue);
    }

    private void ApplyMomentFilter(long? preserveMomentId = null, bool resetPage = false)
    {
        _filteredMoments.Clear();
        _filteredMoments.AddRange(_allMoments
            .Where(MatchesQueueFilters)
            .ToList());

        var targetPage = resetPage ? 1 : CurrentMomentPage;
        if (preserveMomentId.HasValue)
        {
            var preservedIndex = _filteredMoments.FindIndex(moment => moment.Id == preserveMomentId.Value);
            if (preservedIndex >= 0)
            {
                targetPage = (preservedIndex / MomentPageSize) + 1;
            }
        }

        ApplyMomentPage(targetPage, preserveMomentId);
    }

    private void ApplyMomentPage(int requestedPage, long? preserveMomentId = null)
    {
        var totalFiltered = _filteredMoments.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalFiltered / (double)MomentPageSize));
        var currentPage = Math.Clamp(requestedPage, 1, totalPages);

        CurrentMomentPage = currentPage;
        TotalMomentPages = totalPages;
        MomentPageSummary = totalFiltered == 0
            ? "0 moments"
            : $"{((currentPage - 1) * MomentPageSize) + 1}-{Math.Min(totalFiltered, currentPage * MomentPageSize)} of {totalFiltered} moments";

        Moments.Clear();
        foreach (var moment in _filteredMoments
                     .Skip((currentPage - 1) * MomentPageSize)
                     .Take(MomentPageSize))
        {
            Moments.Add(moment);
        }

        SelectedMoment = preserveMomentId.HasValue
            ? Moments.FirstOrDefault(moment => moment.Id == preserveMomentId.Value) ?? Moments.FirstOrDefault()
            : Moments.FirstOrDefault();

        OnPropertyChanged(nameof(CanGoToPreviousMomentPage));
        OnPropertyChanged(nameof(CanGoToNextMomentPage));
        OnPropertyChanged(nameof(HasMultipleMomentPages));
        OnPropertyChanged(nameof(SelectedMomentProgressText));
        PreviousMomentPageCommand.NotifyCanExecuteChanged();
        NextMomentPageCommand.NotifyCanExecuteChanged();
        SelectPreviousMomentCommand.NotifyCanExecuteChanged();
        SelectNextMomentCommand.NotifyCanExecuteChanged();
    }

    private bool MatchesQueueFilters(CoachMomentItem moment)
    {
        return MatchesSelectedObjective(moment) && MatchesSelectedManualLabel(moment);
    }

    private bool MatchesSelectedObjective(CoachMomentItem moment)
    {
        if (SelectedQueueObjectiveKey == "__all__")
        {
            return true;
        }

        var attachedObjective = moment.AttachedObjectiveId?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(SelectedQueueObjectiveKey))
        {
            return string.IsNullOrWhiteSpace(attachedObjective) && string.IsNullOrWhiteSpace(moment.AttachedObjectiveTitle);
        }

        return string.Equals(attachedObjective, SelectedQueueObjectiveKey, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSelectedManualLabel(CoachMomentItem moment)
    {
        return SelectedQueueManualLabelKey switch
        {
            "__all__" => true,
            "__none__" => !moment.HasManualLabel,
            "__has__" => moment.HasManualLabel,
            _ => true,
        };
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousMomentPage))]
    private void PreviousMomentPage()
    {
        if (!CanGoToPreviousMomentPage)
        {
            return;
        }

        ApplyMomentPage(CurrentMomentPage - 1);
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextMomentPage))]
    private void NextMomentPage()
    {
        if (!CanGoToNextMomentPage)
        {
            return;
        }

        ApplyMomentPage(CurrentMomentPage + 1);
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousMoment))]
    private void SelectPreviousMoment()
    {
        MoveSelection(-1);
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextMoment))]
    private void SelectNextMoment()
    {
        MoveSelection(1);
    }

    private async Task<List<CoachOptionItem>> LoadObjectiveOptionsAsync()
    {
        var rows = await _objectivesRepository.GetAllAsync();
        var options = new List<CoachOptionItem>
        {
            new() { Key = "", Label = "None / Unattached" }
        };

        foreach (var row in rows)
        {
            if (row.Id <= 0 || string.IsNullOrWhiteSpace(row.Title))
            {
                continue;
            }

            var key = row.Id.ToString();
            var phaseLabel = ObjectivePhases.ToDisplayLabel(row.Phase);
            var label = row.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
                ? $"{row.Title} ({phaseLabel})"
                : $"{row.Title} ({phaseLabel}) [{row.Status}]";

            options.Add(new CoachOptionItem
            {
                Key = key,
                Label = label
            });
        }

        return options;
    }

    private void RebuildObjectiveCollections(IReadOnlyList<CoachOptionItem> objectiveOptions)
    {
        var previousAttachment = SelectedAttachedObjectiveId;
        var previousFilter = SelectedQueueObjectiveKey;

        ObjectiveOptions.Clear();
        foreach (var option in objectiveOptions)
        {
            ObjectiveOptions.Add(option);
        }

        QueueObjectiveOptions.Clear();
        QueueObjectiveOptions.Add(new CoachOptionItem { Key = "__all__", Label = "All Clips" });
        QueueObjectiveOptions.Add(new CoachOptionItem { Key = "", Label = "Unattached" });
        foreach (var option in objectiveOptions.Where(option => !string.IsNullOrWhiteSpace(option.Key)))
        {
            QueueObjectiveOptions.Add(option);
        }

        SelectedAttachedObjectiveId = ObjectiveOptions.Any(option => option.Key == previousAttachment)
            ? previousAttachment
            : "";
        SelectedQueueObjectiveKey = QueueObjectiveOptions.Any(option => option.Key == previousFilter)
            ? previousFilter
            : "__all__";
    }

    private void ApplyTrainingStatus(CoachTrainingStatus status)
    {
        IsTrainingInProgress = status.IsTrainingInProgress;

        if (status.IsTrainingInProgress && !string.IsNullOrWhiteSpace(status.ActiveTrainingStatusText))
        {
            StatusText = ToUiCopy(status.ActiveTrainingStatusText);
        }

        if (status.LastTrainingCompletedAt.HasValue
            && status.LastTrainingCompletedAt != _lastSeenTrainingCompletedAt
            && !string.IsNullOrWhiteSpace(status.LastTrainingSummary))
        {
            _lastSeenTrainingCompletedAt = status.LastTrainingCompletedAt;
            ModelOutputTitle = status.LastTrainingSucceeded ? "Coach model ready" : "Training failed";
            ModelOutputSummary = ToUiCopy(status.LastTrainingSummary);
            StatusText = status.LastTrainingSucceeded
                ? ToUiCopy(status.Summary)
                : ToUiCopy(status.LastTrainingSummary);
        }
    }

    private void EnsureTrainingMonitor()
    {
        if (!IsTrainingInProgress)
        {
            return;
        }

        if (_trainingMonitorTask is not null && !_trainingMonitorTask.IsCompleted)
        {
            return;
        }

        _trainingMonitorTask = MonitorTrainingAsync();
    }

    private async Task MonitorTrainingAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                var status = await _coachTrainingService.GetStatusAsync();
                ApplyTrainingStatus(status);

                if (!status.IsTrainingInProgress)
                {
                    await RefreshAsync(SelectedMoment?.Id);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Coach training monitor stopped unexpectedly");
        }
    }

    private int GetSelectedFilteredIndex()
    {
        if (SelectedMoment is null)
        {
            return -1;
        }

        return _filteredMoments.FindIndex(moment => moment.Id == SelectedMoment.Id);
    }

    private long? GetAdjacentMomentId(int offset)
    {
        var index = GetSelectedFilteredIndex();
        if (index < 0)
        {
            return null;
        }

        var targetIndex = index + offset;
        if (targetIndex < 0 || targetIndex >= _filteredMoments.Count)
        {
            return null;
        }

        return _filteredMoments[targetIndex].Id;
    }

    private void MoveSelection(int offset)
    {
        var targetMomentId = GetAdjacentMomentId(offset);
        if (!targetMomentId.HasValue)
        {
            return;
        }

        ApplyMomentFilter(targetMomentId.Value, resetPage: false);
    }

    private string BuildSelectionProgressText()
    {
        if (SelectedMoment is null)
        {
            return "";
        }

        var index = GetSelectedFilteredIndex();
        return index < 0 ? "" : $"Clip {index + 1} of {_filteredMoments.Count}";
    }

    private async Task SaveReviewInternalAsync(long? preserveMomentId, string successMessage)
    {
        if (!IsEnabled || SelectedMoment is null)
        {
            StatusText = SelectedMoment is null
                ? "No clip selected. Pick one from the queue first."
                : "Coach Lab is not enabled.";
            return;
        }

        var momentId = SelectedMoment.Id;
        IsBusy = true;
        try
        {
            await _coachLabService.SaveManualLabelAsync(momentId, new CoachManualLabelInput
            {
                LabelQuality = SelectedLabelQuality,
                PrimaryReason = "",
                ObjectiveKey = "",
                AttachedObjectiveId = ParseObjectiveId(SelectedAttachedObjectiveId),
                Explanation = SelectedExplanation,
                Confidence = SelectedConfidence,
            });

            await RefreshAsync(preserveMomentId ?? momentId);
            StatusText = successMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving coach label failed for moment {MomentId}", momentId);
            StatusText = $"Saving review failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static long? ParseObjectiveId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new BitmapImage(uri);
    }

    private Task OpenImagePathAsync(string? path, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (!File.Exists(path))
            {
                StatusText = missingMessage;
                return Task.CompletedTask;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open coach image asset {Path}", path);
            StatusText = $"Failed to open image: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    private static string ToUiCopy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return text
            .Replace("Gemma 4 E4B", "the coach model", StringComparison.OrdinalIgnoreCase)
            .Replace("Gemma-only", "coach", StringComparison.OrdinalIgnoreCase)
            .Replace("Gemma clip card", "draft", StringComparison.OrdinalIgnoreCase)
            .Replace("Gemma coach", "coach", StringComparison.OrdinalIgnoreCase)
            .Replace("Gemma model", "coach model", StringComparison.OrdinalIgnoreCase)
            .Replace("Gemma", "model", StringComparison.OrdinalIgnoreCase)
            .Replace("gemma", "model", StringComparison.OrdinalIgnoreCase);
    }
}

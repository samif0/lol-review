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
        ? $"{LabelQuality} | {DisplayOrFallback(LabelPrimaryReason)} | {(int)Math.Round(LabelConfidence * 100)}%"
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
}

public partial class CoachLabViewModel : ObservableObject
{
    private readonly ICoachLabService _coachLabService;
    private readonly ICoachTrainingService _coachTrainingService;
    private readonly IObjectivesRepository _objectivesRepository;
    private readonly INavigationService _navigationService;
    private readonly ILogger<CoachLabViewModel> _logger;
    private readonly List<CoachMomentItem> _allMoments = [];
    private Task? _trainingMonitorTask;
    private long? _lastSeenTrainingCompletedAt;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isTrainingInProgress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _activeObjectiveTitle = "Observe lane phase";
    [ObservableProperty] private string _modeText = "Assist mode";
    [ObservableProperty] private string _recommendationTitle = "Assist mode active";
    [ObservableProperty] private string _recommendationSummary = "";
    [ObservableProperty] private string _watchItemTitle = "Clip-backed evidence";
    [ObservableProperty] private string _watchItemSummary = "";
    [ObservableProperty] private int _totalMoments;
    [ObservableProperty] private int _reviewedMoments;
    [ObservableProperty] private int _pendingMoments;
    [ObservableProperty] private string _trainingSummary = "";
    [ObservableProperty] private bool _hasPrototypeModel;
    [ObservableProperty] private string _modelOutputTitle = "Coach Read";
    [ObservableProperty] private string _modelOutputSummary = "Train the coach on a few reviewed clips, then ask it what recurring problems it sees or whether your current objective still looks right.";
    [ObservableProperty] private CoachMomentItem? _selectedMoment;
    [ObservableProperty] private string _selectedLabelQuality = "neutral";
    [ObservableProperty] private string _selectedPrimaryReason = "";
    [ObservableProperty] private string _selectedAttachedObjectiveId = "";
    [ObservableProperty] private string _selectedExplanation = "";
    [ObservableProperty] private double _selectedConfidence = 0.7;
    [ObservableProperty] private BitmapImage? _selectedStoryboardImage;
    [ObservableProperty] private BitmapImage? _selectedMinimapImage;
    [ObservableProperty] private string _selectedQueueObjectiveKey = "__all__";
    [ObservableProperty] private bool _canCreateSuggestedObjective;

    public ObservableCollection<CoachMomentItem> Moments { get; } = new();
    public ObservableCollection<CoachOptionItem> QualityOptions { get; } =
    [
        new() { Key = "bad", Label = "Bad" },
        new() { Key = "neutral", Label = "Neutral" },
        new() { Key = "good", Label = "Good" },
    ];
    public ObservableCollection<CoachOptionItem> ObjectiveOptions { get; } = new();
    public ObservableCollection<CoachOptionItem> QueueObjectiveOptions { get; } = new();

    public bool HasSelection => SelectedMoment is not null;
    public bool IsWorking => IsBusy || IsTrainingInProgress;
    public string SelectedMomentChampion => SelectedMoment?.Champion ?? "";
    public string SelectedMomentDraftRationale => SelectedMoment?.DraftRationale ?? "";
    public string SelectedMomentNoteText => SelectedMoment?.NoteText ?? "";
    public string SelectedMomentContextText => SelectedMoment?.ContextText ?? "";
    public string SelectedMomentTimeText => SelectedMoment?.TimeText ?? "";
    public string SelectedMomentSourceText => SelectedMoment?.SourceBadge ?? "";
    public string SelectedConfidenceText => $"Confidence: {SelectedConfidence:F2}";
    public string SelectedPrimaryReasonText => string.IsNullOrWhiteSpace(SelectedPrimaryReason)
        ? "Needs review"
        : SelectedPrimaryReason.Replace('_', ' ');
    public string SuggestedObjectiveButtonText => "Add Suggested Objective";

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

        if (value is null)
        {
            SelectedLabelQuality = "neutral";
            SelectedPrimaryReason = "";
            SelectedAttachedObjectiveId = "";
            SelectedExplanation = "";
            SelectedConfidence = 0.7;
            SelectedStoryboardImage = null;
            SelectedMinimapImage = null;
            return;
        }

        SelectedLabelQuality = value.HasManualLabel ? value.LabelQuality : value.DraftQuality;
        SelectedPrimaryReason = value.HasManualLabel ? value.LabelPrimaryReason : value.DraftPrimaryReason;
        SelectedAttachedObjectiveId = value.AttachedObjectiveId?.ToString() ?? "";
        SelectedExplanation = value.HasManualLabel ? value.LabelExplanation : "";
        SelectedConfidence = value.HasManualLabel
            ? Math.Clamp(value.LabelConfidence, 0.1, 1.0)
            : Math.Clamp(value.DraftConfidence, 0.2, 1.0);
        SelectedStoryboardImage = LoadBitmap(value.StoryboardPath);
        SelectedMinimapImage = LoadBitmap(value.MinimapStripPath);
        OnPropertyChanged(nameof(SelectedPrimaryReasonText));
    }

    partial void OnSelectedConfidenceChanged(double value)
    {
        OnPropertyChanged(nameof(SelectedConfidenceText));
    }

    partial void OnSelectedPrimaryReasonChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedPrimaryReasonText));
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
        ApplyMomentFilter(SelectedMoment?.Id);
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
            StatusText = $"Imported {result.ManualClipsImported} manual clip(s) and drafted {result.DraftsCreated} assist label(s).";
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
    private async Task RefreshAssistAsync()
    {
        if (!IsEnabled) return;

        IsBusy = true;
        try
        {
            StatusText = "Refreshing assist-mode recommendation...";
            await _coachLabService.RefreshRecommendationAsync();
            await RefreshAsync();
            StatusText = "Assist-mode summary refreshed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assist refresh failed");
            StatusText = $"Assist refresh failed: {ex.Message}";
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
            StatusText = "Training premature prototype from prepared clips...";
            var result = await _coachTrainingService.TrainPrematureModelAsync();
            await RefreshAsync();
            EnsureTrainingMonitor();
            ModelOutputTitle = result.AlreadyRunning ? "Training already running" : "Premature prototype training";
            ModelOutputSummary = result.Summary;
            StatusText = result.Summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Premature coach training failed");
            StatusText = $"Premature coach training failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveLabelAsync()
    {
        if (!IsEnabled || SelectedMoment is null) return;

        IsBusy = true;
        try
        {
            await _coachLabService.SaveManualLabelAsync(SelectedMoment.Id, new CoachManualLabelInput
            {
                LabelQuality = SelectedLabelQuality,
                PrimaryReason = "",
                ObjectiveKey = "",
                AttachedObjectiveId = ParseObjectiveId(SelectedAttachedObjectiveId),
                Explanation = SelectedExplanation,
                Confidence = SelectedConfidence,
            });

            await RefreshAsync(SelectedMoment.Id);
            StatusText = "Manual coach label saved.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving coach label failed");
            StatusText = $"Saving coach label failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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
            ModelOutputTitle = report.Title;
            ModelOutputSummary = report.Summary;
            StatusText = report.UsesTrainedModel
                ? "Model problem read generated."
                : report.Title;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model problem query failed");
            StatusText = $"Model problem query failed: {ex.Message}";
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
            ModelOutputTitle = suggestion.Title;
            ModelOutputSummary = suggestion.Summary;

            if (suggestion.AttachedObjectiveId.HasValue)
            {
                var objectiveKey = suggestion.AttachedObjectiveId.Value.ToString();
                if (QueueObjectiveOptions.Any(option => option.Key == objectiveKey))
                {
                    SelectedQueueObjectiveKey = objectiveKey;
                }
            }

            StatusText = suggestion.UsesTrainedModel
                ? "Coach objective read generated."
                : suggestion.Title;
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
                description: _latestSuggestion.CandidateDescription);

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
        var queue = await _coachLabService.GetMomentQueueAsync();
        var objectiveOptions = await LoadObjectiveOptionsAsync();

        ActiveObjectiveTitle = dashboard.ActiveObjectiveTitle;
        ModeText = dashboard.TrainingStatus.HasPersonalAdapter
            ? "Personalized coach"
            : dashboard.TrainingStatus.HasBaseJudge
                ? "Trained coach"
                : dashboard.TrainingStatus.HasTeacherModel
                    ? "Teacher-assisted coach"
                    : dashboard.TrainingStatus.HasPrematurePrototype
                        ? "Prototype coach"
                        : "Assist mode";
        RecommendationTitle = dashboard.RecommendationTitle;
        RecommendationSummary = dashboard.RecommendationSummary;
        WatchItemTitle = dashboard.WatchItemTitle;
        WatchItemSummary = dashboard.WatchItemSummary;
        TotalMoments = dashboard.TotalMoments;
        ReviewedMoments = dashboard.GoldMoments;
        PendingMoments = dashboard.PendingMoments;
        TrainingSummary = dashboard.TrainingStatus.Summary;
        HasPrototypeModel = dashboard.TrainingStatus.HasPrematurePrototype
            || dashboard.TrainingStatus.HasTeacherModel
            || dashboard.TrainingStatus.HasBaseJudge
            || dashboard.TrainingStatus.HasPersonalAdapter;
        ApplyTrainingStatus(dashboard.TrainingStatus);
        RebuildObjectiveCollections(objectiveOptions);

        if (!HasPrototypeModel)
        {
            ModelOutputTitle = "Coach Read";
            ModelOutputSummary = "Train the coach on a few reviewed clips, then ask it what recurring problems it sees or whether your current objective still looks right.";
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

        ApplyMomentFilter(preserveMomentId);
    }

    private void ApplyMomentFilter(long? preserveMomentId = null)
    {
        var filtered = _allMoments
            .Where(MatchesSelectedObjective)
            .ToList();

        Moments.Clear();
        foreach (var moment in filtered)
        {
            Moments.Add(moment);
        }

        SelectedMoment = preserveMomentId.HasValue
            ? Moments.FirstOrDefault(moment => moment.Id == preserveMomentId.Value) ?? Moments.FirstOrDefault()
            : Moments.FirstOrDefault();
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
            var label = row.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
                ? row.Title
                : $"{row.Title} [{row.Status}]";

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
            StatusText = status.ActiveTrainingStatusText;
        }

        if (status.LastTrainingCompletedAt.HasValue
            && status.LastTrainingCompletedAt != _lastSeenTrainingCompletedAt
            && !string.IsNullOrWhiteSpace(status.LastTrainingSummary))
        {
            _lastSeenTrainingCompletedAt = status.LastTrainingCompletedAt;
            ModelOutputTitle = status.LastTrainingSucceeded ? "Coach model ready" : "Training failed";
            ModelOutputSummary = status.LastTrainingSummary;
            StatusText = status.LastTrainingSummary;
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
}

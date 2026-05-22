#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.ViewModels;

/// <summary>One game row in the objective games list.</summary>
public sealed class ObjectiveGameRow
{
    public long GameId { get; init; }
    public bool Practiced { get; init; }
    public string ExecutionNote { get; init; } = "";
    public string ChampionName { get; init; } = "";
    public bool Win { get; init; }
    public string DateText { get; init; } = "";
    public string KdaText { get; init; } = "";
    public bool HasReview { get; init; }
    public bool HasExecutionNote { get; init; }

    public string ResultText => Win ? "W" : "L";
    public string PracticedText => Practiced ? "Practiced" : "Skipped";
    public SolidColorBrush PracticedBackgroundBrush => Practiced
        ? AppSemanticPalette.Brush(AppSemanticPalette.PositiveDimHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.NeutralDimHex);
    public SolidColorBrush PracticedForegroundBrush => Practiced
        ? AppSemanticPalette.Brush(AppSemanticPalette.PositiveHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.NeutralHex);
}

public sealed class ObjectiveEvidenceRow
{
    public long GameId { get; init; }
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    public string ChampionName { get; init; } = "";
    public string DateText { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string Polarity { get; init; } = EvidencePolarities.Neutral;
    public string Status { get; init; } = EvidenceStatuses.NeedsReview;

    public string MetaText => string.Join("  /  ", new[] { ChampionName, DateText, TimeText }.Where(static text => !string.IsNullOrWhiteSpace(text)));

    public string DisplayNote
    {
        get
        {
            var note = (Note ?? "").Trim();
            if (string.IsNullOrWhiteSpace(note))
            {
                return "";
            }

            var title = (Title ?? "").Trim();
            return string.Equals(note, title, StringComparison.OrdinalIgnoreCase)
                ? ""
                : note;
        }
    }

    public bool HasDisplayNote => !string.IsNullOrWhiteSpace(DisplayNote);

    public string PolarityLabel => EvidencePolarities.Normalize(Polarity) switch
    {
        EvidencePolarities.Good => "Good example",
        EvidencePolarities.Bad => "Bad example",
        _ => "Neutral",
    };

    public SolidColorBrush AccentBrush => AppSemanticPalette.Brush(EvidencePolarities.Normalize(Polarity) switch
    {
        EvidencePolarities.Good => AppSemanticPalette.PositiveHex,
        EvidencePolarities.Bad => AppSemanticPalette.NegativeHex,
        _ => AppSemanticPalette.NeutralHex,
    });
}

/// <summary>ViewModel for the Objective Games page — shows all games linked to one objective.</summary>
public partial class ObjectiveGamesViewModel : ObservableObject
{
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _objectiveTitle = "";

    [ObservableProperty]
    private string _objectiveStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    private bool _hasGames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    private bool _hasEvidence;

    [ObservableProperty]
    private int _practicedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string _evidenceSummary = "";

    private long _objectiveId;

    public ObservableCollection<ObjectiveGameRow> Games { get; } = new();

    public ObservableCollection<ObjectiveEvidenceRow> Evidence { get; } = new();

    public bool HasActivity => HasGames || HasEvidence;

    public ObjectiveGamesViewModel(
        IObjectivesRepository objectivesRepo,
        IEvidenceRepository evidenceRepo,
        INavigationService navigationService)
    {
        _objectivesRepo = objectivesRepo;
        _evidenceRepo = evidenceRepo;
        _navigationService = navigationService;
    }

    [RelayCommand]
    private async Task LoadAsync(long objectiveId)
    {
        _objectiveId = objectiveId;
        IsLoading = true;
        using var perf = PerformanceTrace.Time("ObjectiveGames.Load", $"objectiveId={objectiveId}");
        try
        {
            var objective = await _objectivesRepo.GetAsync(objectiveId);
            if (objective is not null)
            {
                ObjectiveTitle = objective.Title;
                var statusText = objective.Status == "active" ? "Active" : "Completed";
                ObjectiveStatus = $"{statusText} \u2022 {ObjectivePhases.ToDisplayLabel(objective.Phase)}";
            }

            var entries = await _objectivesRepo.GetGamesForObjectiveAsync(objectiveId);
            Games.Clear();

            foreach (var entry in entries)
            {
                var date = entry.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(entry.Timestamp).LocalDateTime.ToString("MMM d, yyyy")
                    : "";

                var kda = $"{entry.Kills:F0}/{entry.Deaths:F0}/{entry.Assists:F0}";

                Games.Add(new ObjectiveGameRow
                {
                    GameId = entry.GameId,
                    Practiced = entry.Practiced,
                    ExecutionNote = entry.ExecutionNote,
                    ChampionName = entry.ChampionName,
                    Win = entry.Win,
                    DateText = date,
                    KdaText = kda,
                    HasReview = entry.HasReview,
                    HasExecutionNote = !string.IsNullOrWhiteSpace(entry.ExecutionNote),
                });
            }

            TotalCount = Games.Count;
            PracticedCount = Games.Count(g => g.Practiced);
            HasGames = Games.Count > 0;

            var evidenceRows = await _evidenceRepo.GetForObjectiveAsync(objectiveId);
            Evidence.Clear();
            foreach (var evidence in evidenceRows)
            {
                var date = evidence.GameTimestamp is long ts && ts > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("MMM d, yyyy")
                    : "";

                Evidence.Add(new ObjectiveEvidenceRow
                {
                    GameId = evidence.GameId,
                    Title = evidence.Title,
                    Note = evidence.Note,
                    ChampionName = evidence.ChampionName,
                    DateText = date,
                    TimeText = evidence.StartTimeSeconds is int start ? VodPlayerViewModel.FormatTime(start) : "",
                    Polarity = evidence.Polarity,
                    Status = evidence.Status,
                });
            }

            HasEvidence = Evidence.Count > 0;
            if (HasEvidence)
            {
                var good = Evidence.Count(e => EvidencePolarities.Normalize(e.Polarity) == EvidencePolarities.Good);
                var bad = Evidence.Count(e => EvidencePolarities.Normalize(e.Polarity) == EvidencePolarities.Bad);
                var neutral = Evidence.Count - good - bad;
                EvidenceSummary = $"{Evidence.Count} evidence item(s)  /  {good} good  /  {bad} bad  /  {neutral} neutral";
            }
            else
            {
                EvidenceSummary = "No linked evidence yet.";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenReview(long gameId)
    {
        _navigationService.NavigateTo("review", gameId);
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigationService.GoBack();
    }
}

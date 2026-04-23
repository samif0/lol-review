#nullable enable

using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.App.Contracts;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.App.ViewModels;

/// <summary>Review-note row: one entry per linked game that has review notes.</summary>
public sealed class ObjectiveReviewNoteRow
{
    public long GameId { get; init; }
    public string Header { get; init; } = "";
    public string Notes { get; init; } = "";
}

/// <summary>Execution-note row: the per-game note the user wrote on `game_objectives`.</summary>
public sealed class ObjectiveExecutionNoteRow
{
    public long GameId { get; init; }
    public string Header { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>Bookmark/clip row linked to this objective.</summary>
public sealed class ObjectiveBookmarkRow
{
    public long BookmarkId { get; init; }
    public long GameId { get; init; }
    public int GameTimeSeconds { get; init; }
    public string TimeLabel { get; init; } = "";
    public string GameLabel { get; init; } = "";
    public string Note { get; init; } = "";
    public string Tags { get; init; } = "";
    public bool HasNote { get; init; }
    public bool HasTags { get; init; }
    public bool HasClip { get; init; }
}

/// <summary>ViewModel for the Objective Notes page — one completed objective's notes, execution-notes, and clips.</summary>
public partial class ObjectiveNotesViewModel : ObservableObject
{
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IVodRepository _vodRepo;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _objectiveTitle = "";

    [ObservableProperty]
    private string _objectiveStatus = "";

    [ObservableProperty]
    private bool _hasReviewNotes;

    [ObservableProperty]
    private bool _hasExecutionNotes;

    [ObservableProperty]
    private bool _hasBookmarks;

    [ObservableProperty]
    private bool _hasAnything;

    public ObservableCollection<ObjectiveReviewNoteRow> ReviewNotes { get; } = new();
    public ObservableCollection<ObjectiveExecutionNoteRow> ExecutionNotes { get; } = new();
    public ObservableCollection<ObjectiveBookmarkRow> Bookmarks { get; } = new();

    public ObjectiveNotesViewModel(
        IObjectivesRepository objectivesRepo,
        IVodRepository vodRepo,
        INavigationService navigationService)
    {
        _objectivesRepo = objectivesRepo;
        _vodRepo = vodRepo;
        _navigationService = navigationService;
    }

    [RelayCommand]
    private async Task LoadAsync(long objectiveId)
    {
        IsLoading = true;
        try
        {
            var objective = await _objectivesRepo.GetAsync(objectiveId);
            if (objective is not null)
            {
                ObjectiveTitle = objective.Title;
                var statusText = objective.Status == "active" ? "Active" : "Completed";
                ObjectiveStatus = $"{statusText} \u2022 {ObjectivePhases.ToDisplayLabel(objective.Phase)}";
            }

            ReviewNotes.Clear();
            ExecutionNotes.Clear();
            Bookmarks.Clear();

            var games = await _objectivesRepo.GetGamesForObjectiveAsync(objectiveId);
            var gameLabels = new Dictionary<long, string>();
            foreach (var g in games)
            {
                var date = g.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(g.Timestamp).LocalDateTime.ToString("MMM d, yyyy")
                    : "";
                var result = g.Win ? "W" : "L";
                var header = $"{result} \u2022 {g.ChampionName} \u2022 {date}";
                gameLabels[g.GameId] = header;

                if (!string.IsNullOrWhiteSpace(g.ReviewNotes))
                {
                    ReviewNotes.Add(new ObjectiveReviewNoteRow
                    {
                        GameId = g.GameId,
                        Header = header,
                        Notes = g.ReviewNotes.Trim(),
                    });
                }

                if (!string.IsNullOrWhiteSpace(g.ExecutionNote))
                {
                    ExecutionNotes.Add(new ObjectiveExecutionNoteRow
                    {
                        GameId = g.GameId,
                        Header = header,
                        Note = g.ExecutionNote.Trim(),
                    });
                }
            }

            var bookmarks = await _vodRepo.GetBookmarksForObjectiveAsync(objectiveId);
            foreach (var b in bookmarks)
            {
                var tagsText = JoinTags(b.TagsJson);
                Bookmarks.Add(new ObjectiveBookmarkRow
                {
                    BookmarkId = b.Id,
                    GameId = b.GameId,
                    GameTimeSeconds = b.GameTimeSeconds,
                    TimeLabel = FormatClockTime(b.GameTimeSeconds),
                    GameLabel = gameLabels.TryGetValue(b.GameId, out var lbl) ? lbl : $"Game #{b.GameId}",
                    Note = b.Note ?? "",
                    Tags = tagsText,
                    HasNote = !string.IsNullOrWhiteSpace(b.Note),
                    HasTags = !string.IsNullOrEmpty(tagsText),
                    HasClip = !string.IsNullOrEmpty(b.ClipPath),
                });
            }

            HasReviewNotes = ReviewNotes.Count > 0;
            HasExecutionNotes = ExecutionNotes.Count > 0;
            HasBookmarks = Bookmarks.Count > 0;
            HasAnything = HasReviewNotes || HasExecutionNotes || HasBookmarks;
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
    private void PlayBookmark(ObjectiveBookmarkRow row)
    {
        _navigationService.NavigateTo("vodplayer", new VodPlayerNavigationRequest
        {
            GameId = row.GameId,
            SeekTimeS = row.GameTimeSeconds,
        });
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigationService.GoBack();
    }

    private static string FormatClockTime(int seconds)
    {
        var m = seconds / 60;
        var s = seconds % 60;
        return $"{m}:{s:D2}";
    }

    private static string JoinTags(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return "";
        try
        {
            var tags = JsonSerializer.Deserialize<List<string>>(tagsJson);
            if (tags is null || tags.Count == 0) return "";
            return string.Join(", ", tags);
        }
        catch
        {
            return "";
        }
    }
}

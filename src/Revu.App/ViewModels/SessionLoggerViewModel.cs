#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Services;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Models;

namespace Revu.App.ViewModels;

/// <summary>Display model for a single game entry in the session log.</summary>
public sealed class SessionGameEntry
{
    public long GameId { get; init; }
    public string ChampionName { get; init; } = "";
    public string EnemyChampion { get; init; } = "";
    public bool Win { get; init; }
    public int MentalRating { get; init; } = 5;
    public string ImprovementNote { get; init; } = "";
    public bool RuleBroken { get; init; }
    public bool HasReview { get; init; }
    public string ResultText => Win ? "W" : "L";
    public string MentalText => $"Mental: {MentalRating}/10";
    public bool HasImprovementNote => !string.IsNullOrWhiteSpace(ImprovementNote);

    /// <summary>"Kai'Sa vs Tristana" when known, otherwise just "Kai'Sa".</summary>
    public string ChampionDisplay => string.IsNullOrWhiteSpace(EnemyChampion)
        ? ChampionName
        : $"{ChampionName} vs {EnemyChampion}";
}

/// <summary>ViewModel for the Session Logger page.</summary>
public partial class SessionLoggerViewModel : ObservableObject
{
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IGameRepository _gameRepo;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private DateOnly _currentDate = DateOnly.FromDateTime(DateTime.Now);

    [ObservableProperty]
    private string _dateDisplay = "";

    [ObservableProperty]
    private bool _isToday = true;

    [ObservableProperty]
    private string _gamesHeading = "TODAY'S GAMES";

    // Stats
    [ObservableProperty]
    private int _totalGames;

    [ObservableProperty]
    private int _wins;

    [ObservableProperty]
    private int _losses;

    [ObservableProperty]
    private string _avgMental = "\u2014";

    [ObservableProperty]
    private int _ruleBreaks;

    [ObservableProperty]
    private int _adherenceStreak;

    // Session intention
    [ObservableProperty]
    private string _sessionIntention = "";

    [ObservableProperty]
    private bool _hasIntention;

    [ObservableProperty]
    private int _debriefRating;

    // Navigation
    [ObservableProperty]
    private bool _canNavigateForward;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasGames;

    [ObservableProperty]
    private bool _hasNeedsReviewGames;

    [ObservableProperty]
    private bool _hasReviewedGames;

    [ObservableProperty]
    private int _needsReviewCount;

    [ObservableProperty]
    private int _reviewedCount;

    [ObservableProperty]
    private string _needsReviewCountText = "0 games";

    [ObservableProperty]
    private string _reviewedCountText = "0 games";

    [ObservableProperty]
    private string _emptyMessage = "No games logged today.\nGames are logged automatically when detected.";

    public ObservableCollection<SessionGameEntry> Games { get; } = new();
    public ObservableCollection<SessionGameEntry> NeedsReviewGames { get; } = new();
    public ObservableCollection<SessionGameEntry> ReviewedGames { get; } = new();

    public SessionLoggerViewModel(
        ISessionLogRepository sessionLogRepo,
        IGameRepository gameRepo,
        INavigationService navigationService,
        IDialogService dialogService)
    {
        _sessionLogRepo = sessionLogRepo;
        _gameRepo = gameRepo;
        _navigationService = navigationService;
        _dialogService = dialogService;
    }

    [RelayCommand]
    private async Task DeleteGameAsync(long gameId)
    {
        var game = ReviewedGames.FirstOrDefault(g => g.GameId == gameId)
                   ?? NeedsReviewGames.FirstOrDefault(g => g.GameId == gameId);
        var champ = game?.ChampionName ?? "this game";
        var outcome = game?.Win == true ? "W" : game?.Win == false ? "L" : "";
        var label = string.IsNullOrEmpty(outcome) ? champ : $"{champ} ({outcome})";

        var confirmed = await _dialogService.ShowConfirmAsync(
            $"Delete {label}?",
            "This permanently removes the game, its review, and all practice " +
            "tracking from your stats. Clips extracted from this game are also " +
            "deleted. The VOD recording itself is left in your Ascent folder.\n\n" +
            "A database backup is saved automatically before deletion. " +
            "This cannot be undone from inside the app.");
        if (!confirmed) return;

        try
        {
            await _gameRepo.DeleteAsync(gameId);

            var reviewed = ReviewedGames.FirstOrDefault(g => g.GameId == gameId);
            if (reviewed is not null) ReviewedGames.Remove(reviewed);
            var needs = NeedsReviewGames.FirstOrDefault(g => g.GameId == gameId);
            if (needs is not null) NeedsReviewGames.Remove(needs);
            HasReviewedGames = ReviewedGames.Count > 0;
            HasNeedsReviewGames = NeedsReviewGames.Count > 0;

            WeakReferenceMessenger.Default.Send(new GameDeletedMessage(gameId));
        }
        catch
        {
            await _dialogService.ShowConfirmAsync(
                "Delete failed",
                "Couldn't delete the game. Your database backup is still safe on disk.");
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await RefreshDataAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PreviousDayAsync()
    {
        CurrentDate = CurrentDate.AddDays(-1);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task NextDayAsync()
    {
        if (!IsToday)
        {
            CurrentDate = CurrentDate.AddDays(1);
            await RefreshDataAsync();
        }
    }

    [RelayCommand]
    private async Task GoTodayAsync()
    {
        CurrentDate = DateOnly.FromDateTime(DateTime.Now);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task SaveIntentionAsync()
    {
        if (!string.IsNullOrWhiteSpace(SessionIntention))
        {
            await _sessionLogRepo.SetSessionIntentionAsync(
                CurrentDate.ToString("yyyy-MM-dd"), SessionIntention.Trim());
        }
    }

    [RelayCommand]
    private void NavigateToReview(long gameId)
    {
        // Navigate to game review page with the game ID as parameter
        _navigationService.NavigateTo("review", gameId);
    }

    /// <summary>v2.15.10: clear a false-positive rule break flag for a game.
    /// Used when a since-removed heuristic or the live rules engine flagged
    /// the game incorrectly. Persists via session_log; refreshes the day so
    /// the pill disappears + the day stats roll up correctly.</summary>
    [RelayCommand]
    private async Task ClearRuleBreakAsync(long gameId)
    {
        if (gameId <= 0) return;
        await _sessionLogRepo.SetRuleBrokenAsync(gameId, ruleBroken: false);
        await RefreshDataAsync();
    }

    private async Task RefreshDataAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        IsToday = CurrentDate == today;
        CanNavigateForward = !IsToday;

        // Format date display
        var dt = CurrentDate.ToDateTime(TimeOnly.MinValue);
        var friendly = dt.ToString("dddd, MMM dd");
        DateDisplay = IsToday ? $"{friendly}  (Today)" : friendly;
        GamesHeading = IsToday ? "TODAY'S GAMES" : $"GAMES \u2014 {friendly}";
        EmptyMessage = IsToday
            ? "No games logged today.\nGames are logged automatically when detected."
            : "No games logged on this date.";

        var dateStr = CurrentDate.ToString("yyyy-MM-dd");

        // Load stats
        var stats = await _sessionLogRepo.GetStatsForDateAsync(dateStr);
        TotalGames = stats.Games;
        Wins = stats.Wins;
        Losses = stats.Losses;
        AvgMental = stats.Games > 0 ? $"{stats.AvgMental:F1}" : "\u2014";
        RuleBreaks = stats.RuleBreaks;
        AdherenceStreak = await _sessionLogRepo.GetAdherenceStreakAsync();

        // Load session intention
        var session = await _sessionLogRepo.GetSessionAsync(dateStr);
        if (session != null)
        {
            SessionIntention = session.Intention;
            HasIntention = !string.IsNullOrWhiteSpace(session.Intention);
            DebriefRating = session.DebriefRating;
        }
        else
        {
            SessionIntention = "";
            HasIntention = false;
            DebriefRating = 0;
        }

        // Load games
        var entries = await _sessionLogRepo.GetForDateAsync(dateStr);
        var gameEntries = new List<SessionGameEntry>();

        foreach (var entry in entries)
        {
            bool hasReview = false;
            string enemyChampion = "";
            if (entry.GameId.HasValue)
            {
                var game = await _gameRepo.GetAsync(entry.GameId.Value);
                if (game != null)
                {
                    enemyChampion = game.EnemyLaner ?? "";
                    hasReview = game.Rating > 0
                        || !string.IsNullOrWhiteSpace(game.ReviewNotes)
                        || !string.IsNullOrWhiteSpace(game.Mistakes)
                        || !string.IsNullOrWhiteSpace(game.WentWell)
                        || !string.IsNullOrWhiteSpace(game.FocusNext)
                        || !string.IsNullOrWhiteSpace(game.SpottedProblems)
                        || !string.IsNullOrWhiteSpace(game.OutsideControl)
                        || !string.IsNullOrWhiteSpace(game.WithinControl)
                        || !string.IsNullOrWhiteSpace(game.Attribution)
                        || !string.IsNullOrWhiteSpace(game.PersonalContribution)
                        || !string.IsNullOrWhiteSpace(entry.ImprovementNote)
                        || !string.IsNullOrWhiteSpace(entry.MentalHandled);
                }
            }

            gameEntries.Add(new SessionGameEntry
            {
                GameId = entry.GameId ?? 0,
                ChampionName = entry.ChampionName,
                EnemyChampion = enemyChampion,
                Win = entry.Win,
                MentalRating = entry.MentalRating,
                ImprovementNote = entry.ImprovementNote,
                RuleBroken = entry.RuleBroken != 0,
                HasReview = hasReview,
            });
        }

        var needsReview = gameEntries.Where(static ge => !ge.HasReview).ToList();
        var reviewed = gameEntries.Where(static ge => ge.HasReview).ToList();

        NeedsReviewCount = needsReview.Count;
        ReviewedCount = reviewed.Count;
        NeedsReviewCountText = $"{NeedsReviewCount} game{(NeedsReviewCount == 1 ? "" : "s")}";
        ReviewedCountText = $"{ReviewedCount} game{(ReviewedCount == 1 ? "" : "s")}";

        DispatcherHelper.RunOnUIThread(() =>
        {
            Games.Clear();
            NeedsReviewGames.Clear();
            ReviewedGames.Clear();

            foreach (var ge in gameEntries)
            {
                Games.Add(ge);
            }

            foreach (var ge in needsReview)
            {
                NeedsReviewGames.Add(ge);
            }

            foreach (var ge in reviewed)
            {
                ReviewedGames.Add(ge);
            }

            HasGames = Games.Count > 0;
            HasNeedsReviewGames = NeedsReviewGames.Count > 0;
            HasReviewedGames = ReviewedGames.Count > 0;
        });
    }
}

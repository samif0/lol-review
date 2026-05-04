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

    /// <summary>v2.16: full role→champion JSON for both teams. Drives the
    /// 2v2/jg+mid pairing display when populated.</summary>
    public string ParticipantMapJson { get; init; } = "";

    /// <summary>v2.16: role the user played this specific game (LCU truth)
    /// or the configured PrimaryRole as fallback.</summary>
    public string GameRole { get; init; } = "";

    /// <summary>"Kai'Sa+Nautilus vs Tristana+Renata" when role + map are
    /// available, otherwise the lane-only fallback "Kai'Sa vs Tristana".</summary>
    public string ChampionDisplay => RoleAwareDisplay() ?? LaneOnlyDisplay();

    private string LaneOnlyDisplay() => string.IsNullOrWhiteSpace(EnemyChampion)
        ? ChampionName
        : $"{ChampionName} vs {EnemyChampion}";

    private string? RoleAwareDisplay()
    {
        if (string.IsNullOrWhiteSpace(GameRole) || string.IsNullOrWhiteSpace(ParticipantMapJson))
            return null;

        System.Collections.Generic.Dictionary<string, string>? map = null;
        try
        {
            map = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(ParticipantMapJson);
        }
        catch { return null; }
        if (map is null || map.Count == 0) return null;

        var role = GameRole.ToLowerInvariant();
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
            _ => null,
        };
    }

    private static string? Pair(
        System.Collections.Generic.Dictionary<string, string> map,
        string ownPrimary, string ownPartner,
        string enemyPrimary, string enemyPartner)
    {
        if (!map.TryGetValue(ownPrimary, out var op) || string.IsNullOrEmpty(op)) return null;
        if (!map.TryGetValue(enemyPrimary, out var ep) || string.IsNullOrEmpty(ep)) return null;
        var ownPart = map.TryGetValue(ownPartner, out var v1) ? v1 : "";
        var enemyPart = map.TryGetValue(enemyPartner, out var v2) ? v2 : "";
        var ownStr = string.IsNullOrEmpty(ownPart) ? op : $"{op}+{ownPart}";
        var enemyStr = string.IsNullOrEmpty(enemyPart) ? ep : $"{ep}+{enemyPart}";
        return $"{ownStr} vs {enemyStr}";
    }
}

/// <summary>ViewModel for the Session Logger page.</summary>
public partial class SessionLoggerViewModel : ObservableObject
{
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IGameRepository _gameRepo;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly Revu.Core.Services.IConfigService _configService;
    private readonly Revu.Core.Services.IReviewWorkflowService _reviewWorkflowService;

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
        IDialogService dialogService,
        Revu.Core.Services.IConfigService configService,
        Revu.Core.Services.IReviewWorkflowService reviewWorkflowService)
    {
        _sessionLogRepo = sessionLogRepo;
        _gameRepo = gameRepo;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _configService = configService;
        _reviewWorkflowService = reviewWorkflowService;
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

    /// <summary>Mark a needs-review game reviewed without opening the review
    /// page. Saves an empty review with a neutral mental rating, dropping
    /// the game out of the unreviewed bucket. RequireReviewNotes is bypassed
    /// on purpose — the user is opting out of detail.</summary>
    [RelayCommand]
    private async Task SkipReviewAsync(long gameId)
    {
        var entry = NeedsReviewGames.FirstOrDefault(g => g.GameId == gameId);
        if (entry is null) return;

        var result = await _reviewWorkflowService.SaveAsync(new Revu.Core.Services.SaveReviewRequest(
            GameId: gameId,
            ChampionName: entry.ChampionName,
            Win: entry.Win,
            RequireReviewNotes: false,
            Snapshot: BuildEmptySnapshot()));

        if (!result.Success) return;

        // Stamp is_skipped after the workflow's LogGameAsync runs so
        // AvgMental / mental-trend queries exclude this row's neutral
        // rating from behavioral signal.
        await _sessionLogRepo.MarkSkippedAsync(gameId);

        WeakReferenceMessenger.Default.Send(new GameReviewedMessage(gameId));
        await RefreshDataAsync();
    }

    private static Revu.Core.Services.ReviewSnapshot BuildEmptySnapshot() => new(
        MentalRating: 5,
        WentWell: "",
        Mistakes: "",
        FocusNext: "",
        ReviewNotes: "",
        ImprovementNote: "",
        Attribution: "",
        MentalHandled: "",
        SpottedProblems: "",
        OutsideControl: "",
        WithinControl: "",
        PersonalContribution: "",
        EnemyLaner: "",
        MatchupNote: "",
        SelectedTagIds: Array.Empty<long>(),
        ObjectivePractices: Array.Empty<Revu.Core.Services.SaveObjectivePracticeRequest>());

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
            string participantMap = "";
            string gameRole = "";
            if (entry.GameId.HasValue)
            {
                var game = await _gameRepo.GetAsync(entry.GameId.Value);
                if (game != null)
                {
                    enemyChampion = game.EnemyLaner ?? "";
                    participantMap = game.ParticipantMap ?? "";
                    gameRole = string.IsNullOrWhiteSpace(game.Position)
                        ? _configService.PrimaryRole
                        : game.Position;
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
                ParticipantMapJson = participantMap,
                GameRole = gameRole,
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

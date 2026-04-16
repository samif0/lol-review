#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.Styling;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the Dashboard page — session overview, stats, unreviewed games.</summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly INavigationService _navigationService;
    private readonly ILogger<DashboardViewModel> _logger;

    // ── Observable Properties ───────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _totalGames;

    [ObservableProperty]
    private int _wins;

    [ObservableProperty]
    private int _losses;

    [ObservableProperty]
    private double _avgMental;

    [ObservableProperty]
    private int _adherenceStreak;

    [ObservableProperty]
    private int _winStreak;

    [ObservableProperty]
    private string _greeting = "";

    [ObservableProperty]
    private string _sessionBannerText = "";

    [ObservableProperty]
    private string _sessionBannerColorHex = "#14121E";

    [ObservableProperty]
    private string _sessionBannerTextColorHex = "#F0EEF8";

    [ObservableProperty]
    private bool _showSessionBanner;

    [ObservableProperty]
    private string _lastFocus = "";

    [ObservableProperty]
    private bool _hasLastFocus;

    [ObservableProperty]
    private string _winLossText = "0 / 0";

    [ObservableProperty]
    private string _winLossColorHex = "#F0EEF8";

    [ObservableProperty]
    private string _adherenceColorHex = "#F0EEF8";

    [ObservableProperty]
    private int _unreviewedCount;

    [ObservableProperty]
    private string _unreviewedCountText = "0 games";

    [ObservableProperty]
    private bool _allReviewed;

    public ObservableCollection<GameDisplayItem> TodaysGames { get; } = new();
    public ObservableCollection<GameDisplayItem> UnreviewedGames { get; } = new();
    public ObservableCollection<DashboardObjectiveItem> ActiveObjectives { get; } = new();

    // ── Constructor ─────────────────────────────────────────────────

    public DashboardViewModel(
        IGameRepository gameRepo,
        ISessionLogRepository sessionLogRepo,
        IObjectivesRepository objectivesRepo,
        INavigationService navigationService,
        ILogger<DashboardViewModel> logger)
    {
        _gameRepo = gameRepo;
        _sessionLogRepo = sessionLogRepo;
        _objectivesRepo = objectivesRepo;
        _navigationService = navigationService;
        _logger = logger;
    }

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            // Build greeting
            var hour = DateTime.Now.Hour;
            var tod = hour < 12 ? "morning" : hour < 17 ? "afternoon" : "evening";
            Greeting = $"Good {tod} \u2014 lock in.";

            // Today's session stats
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var stats = await _sessionLogRepo.GetStatsForDateAsync(today);
            TotalGames = stats.Games;
            Wins = stats.Wins;
            Losses = stats.Losses;
            AvgMental = stats.AvgMental;

            WinLossText = $"{Wins} / {Losses}";
            if (TotalGames > 0)
            {
                WinLossColorHex = Wins > Losses ? "#7EC9A0" : Losses > Wins ? "#D38C90" : "#F0EEF8";
            }
            else
            {
                WinLossColorHex = "#F0EEF8";
            }

            // Adherence streak
            AdherenceStreak = await _sessionLogRepo.GetAdherenceStreakAsync();
            AdherenceColorHex = AdherenceStreak >= 3 ? "#7EC9A0" : "#F0EEF8";

            // Win streak
            WinStreak = await _gameRepo.GetWinStreakAsync();

            // Session banner
            if (TotalGames > 0)
            {
                ShowSessionBanner = true;
                if (AvgMental >= 7)
                {
                    SessionBannerText = "Locked in";
                    SessionBannerColorHex = "#0F1E18";
                    SessionBannerTextColorHex = "#7EC9A0";
                }
                else if (AvgMental >= 4)
                {
                    SessionBannerText = "Decent session";
                    SessionBannerColorHex = "#261C12";
                    SessionBannerTextColorHex = "#C9956A";
                }
                else
                {
                    SessionBannerText = "Consider a break";
                    SessionBannerColorHex = "#2A1820";
                    SessionBannerTextColorHex = "#D38C90";
                }
            }
            else
            {
                ShowSessionBanner = false;
            }

            // Last review focus
            var reviewFocus = await _gameRepo.GetLastReviewFocusAsync();
            if (reviewFocus != null && !string.IsNullOrWhiteSpace(reviewFocus.FocusNext))
            {
                LastFocus = reviewFocus.FocusNext;
                HasLastFocus = true;
            }
            else
            {
                LastFocus = "";
                HasLastFocus = false;
            }

            // Active objectives
            var objectives = await _objectivesRepo.GetActiveAsync();
            DispatcherHelper.RunOnUIThread(() =>
            {
                ActiveObjectives.Clear();
                foreach (var obj in objectives)
                {
                    var info = IObjectivesRepository.GetLevelInfo(obj.Score, obj.GameCount);

                    ActiveObjectives.Add(new DashboardObjectiveItem
                    {
                        Title = obj.Title,
                        PhaseLabel = ObjectivePhases.ToDisplayLabel(obj.Phase),
                        LevelName = info.LevelName,
                        Score = obj.Score,
                        GameCount = obj.GameCount,
                        Progress = info.Progress,
                        LevelColorHex = GetLevelColor(info.LevelIndex),
                        InfoText = $"{info.LevelName}  \u2022  {obj.Score} pts  \u2022  {obj.GameCount} games"
                    });
                }
            });

            // Unreviewed games
            var unreviewed = await _gameRepo.GetUnreviewedGamesAsync(days: 3);
            UpdateUnreviewedSummary(unreviewed.Count);

            DispatcherHelper.RunOnUIThread(() =>
            {
                UnreviewedGames.Clear();
                foreach (var game in unreviewed.Take(8))
                {
                    UnreviewedGames.Add(MapGameDisplay(game));
                }
            });

            // Today's games
            var todaysGames = await _gameRepo.GetTodaysGamesAsync();
            DispatcherHelper.RunOnUIThread(() =>
            {
                TodaysGames.Clear();
                foreach (var game in todaysGames)
                {
                    TodaysGames.Add(MapGameDisplay(game));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load dashboard data");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void NavigateToReview(long gameId)
    {
        _navigationService.NavigateTo("review", gameId);
    }

    [RelayCommand]
    private async Task HideGameAsync(long gameId)
    {
        await _gameRepo.SetHiddenAsync(gameId, hidden: true);
        var unreviewedItem = UnreviewedGames.FirstOrDefault(g => g.GameId == gameId);
        if (unreviewedItem is not null) UnreviewedGames.Remove(unreviewedItem);
        var todayItem = TodaysGames.FirstOrDefault(g => g.GameId == gameId);
        if (todayItem is not null) TodaysGames.Remove(todayItem);
        UpdateUnreviewedSummary(UnreviewedGames.Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static GameDisplayItem MapGameDisplay(GameStats game)
    {
        var duration = game.GameDuration > 0
            ? $"{game.GameDuration / 60}:{game.GameDuration % 60:D2}"
            : "";

        var date = game.Timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(game.Timestamp).LocalDateTime.ToString("MMM dd, HH:mm")
            : "";

        return new GameDisplayItem
        {
            GameId = game.GameId,
            ChampionName = game.ChampionName,
            Win = game.Win,
            WinLossText = game.Win ? "W" : "L",
            Kills = game.Kills,
            Deaths = game.Deaths,
            Assists = game.Assists,
            KdaRatio = game.KdaRatio,
            KdaText = $"{game.Kills}/{game.Deaths}/{game.Assists}",
            KdaRatioText = $"({game.KdaRatio:F1})",
            CsTotal = game.CsTotal,
            CsPerMin = game.CsPerMin,
            VisionScore = game.VisionScore,
            TotalDamageToChampions = game.TotalDamageToChampions,
            Duration = duration,
            DatePlayed = date,
            GameMode = game.DisplayGameMode,
            WinLossColorHex = game.Win ? "#7EC9A0" : "#D38C90",
            BorderColorHex = game.Win ? "#7EC9A0" : "#D38C90",
            HasReview = HasPersistedReview(game),
            DamageText = FormatNumber(game.TotalDamageToChampions),
            StatsLine = $"CS {game.CsTotal} ({game.CsPerMin:F1}/m)  \u2022  Vision {game.VisionScore}  \u2022  {FormatNumber(game.TotalDamageToChampions)} dmg"
        };
    }

    private static bool HasPersistedReview(GameStats game)
    {
        return game.Rating > 0
               || !string.IsNullOrWhiteSpace(game.ReviewNotes)
               || !string.IsNullOrWhiteSpace(game.Mistakes)
               || !string.IsNullOrWhiteSpace(game.WentWell)
               || !string.IsNullOrWhiteSpace(game.FocusNext)
               || !string.IsNullOrWhiteSpace(game.SpottedProblems)
               || !string.IsNullOrWhiteSpace(game.OutsideControl)
               || !string.IsNullOrWhiteSpace(game.WithinControl)
               || !string.IsNullOrWhiteSpace(game.Attribution)
               || !string.IsNullOrWhiteSpace(game.PersonalContribution);
    }

    private static string FormatNumber(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString()
    };

    private void UpdateUnreviewedSummary(int count)
    {
        UnreviewedCount = count;
        UnreviewedCountText = $"{count} game{(count != 1 ? "s" : "")}";
        AllReviewed = count == 0;
    }

    private static string GetLevelColor(int levelIndex) =>
        AppSemanticPalette.ObjectiveLevelHex(levelIndex);
}

// ── Display models ──────────────────────────────────────────────────

/// <summary>Flattened game data for display binding in the UI.</summary>
public class GameDisplayItem
{
    public long GameId { get; set; }
    public string ChampionName { get; set; } = "";
    public bool Win { get; set; }
    public string WinLossText { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public double KdaRatio { get; set; }
    public string KdaText { get; set; } = "";
    public string KdaRatioText { get; set; } = "";
    public int CsTotal { get; set; }
    public double CsPerMin { get; set; }
    public int VisionScore { get; set; }
    public int TotalDamageToChampions { get; set; }
    public string Duration { get; set; } = "";
    public string DatePlayed { get; set; } = "";
    public string GameMode { get; set; } = "";
    public string WinLossColorHex { get; set; } = "#F0EEF8";
    public string BorderColorHex { get; set; } = "#24203A";
    public bool HasReview { get; set; }
    public bool HasVod { get; set; }
    public string DamageText { get; set; } = "";
    public string StatsLine { get; set; } = "";
}

/// <summary>Flattened objective data for display binding on the dashboard.</summary>
public class DashboardObjectiveItem
{
    public string Title { get; set; } = "";
    public string PhaseLabel { get; set; } = "";
    public string LevelName { get; set; } = "";
    public int Score { get; set; }
    public int GameCount { get; set; }
    public double Progress { get; set; }
    public string LevelColorHex { get; set; } = "#8A80A8";
    public string InfoText { get; set; } = "";
    public Microsoft.UI.Xaml.Media.SolidColorBrush LevelColorBrush
    {
        get
        {
            var hex = LevelColorHex.TrimStart('#');
            if (hex.Length != 6)
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 138, 128, 168)); // #8A80A8 neutral
            }

            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, r, g, b));
        }
    }
}

#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
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
    private string _sessionBannerColorHex = "#12121a";

    [ObservableProperty]
    private string _sessionBannerTextColorHex = "#e8e8f0";

    [ObservableProperty]
    private bool _showSessionBanner;

    [ObservableProperty]
    private string _lastFocus = "";

    [ObservableProperty]
    private bool _hasLastFocus;

    [ObservableProperty]
    private string _winLossText = "0 / 0";

    [ObservableProperty]
    private string _winLossColorHex = "#e8e8f0";

    [ObservableProperty]
    private string _adherenceColorHex = "#e8e8f0";

    [ObservableProperty]
    private int _unreviewedCount;

    [ObservableProperty]
    private string _unreviewedCountText = "0 games";

    [ObservableProperty]
    private bool _allReviewed;

    [ObservableProperty]
    private string _claudeButtonText = "Copy Claude Context";

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
                WinLossColorHex = Wins > Losses ? "#22c55e" : Losses > Wins ? "#ef4444" : "#e8e8f0";
            }
            else
            {
                WinLossColorHex = "#e8e8f0";
            }

            // Adherence streak
            AdherenceStreak = await _sessionLogRepo.GetAdherenceStreakAsync();
            AdherenceColorHex = AdherenceStreak >= 3 ? "#22c55e" : "#e8e8f0";

            // Win streak
            WinStreak = await _gameRepo.GetWinStreakAsync();

            // Session banner
            if (TotalGames > 0)
            {
                ShowSessionBanner = true;
                if (AvgMental >= 7)
                {
                    SessionBannerText = "Locked in";
                    SessionBannerColorHex = "#0d2a1a";
                    SessionBannerTextColorHex = "#22c55e";
                }
                else if (AvgMental >= 4)
                {
                    SessionBannerText = "Decent session";
                    SessionBannerColorHex = "#2a2a0a";
                    SessionBannerTextColorHex = "#d4c017";
                }
                else
                {
                    SessionBannerText = "Consider a break";
                    SessionBannerColorHex = "#7f1d1d";
                    SessionBannerTextColorHex = "#ef4444";
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
                    var title = obj.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "";
                    var score = obj.TryGetValue("score", out var s) ? System.Convert.ToInt32(s ?? 0) : 0;
                    var gameCount = obj.TryGetValue("game_count", out var gc) ? System.Convert.ToInt32(gc ?? 0) : 0;
                    var info = IObjectivesRepository.GetLevelInfo(score, gameCount);

                    ActiveObjectives.Add(new DashboardObjectiveItem
                    {
                        Title = title,
                        LevelName = info.LevelName,
                        Score = score,
                        GameCount = gameCount,
                        Progress = info.Progress,
                        LevelColorHex = GetLevelColor(info.LevelIndex),
                        InfoText = $"{info.LevelName}  \u2022  {score} pts  \u2022  {gameCount} games"
                    });
                }
            });

            // Unreviewed games
            var unreviewed = await _gameRepo.GetUnreviewedGamesAsync(days: 3);
            System.Diagnostics.Debug.WriteLine($"[Dashboard] Unreviewed games: {unreviewed.Count}");
            foreach (var g in unreviewed.Take(3))
                System.Diagnostics.Debug.WriteLine($"  - {g.ChampionName} {(g.Win ? "W" : "L")} id={g.GameId}");
            UnreviewedCount = unreviewed.Count;
            UnreviewedCountText = $"{unreviewed.Count} game{(unreviewed.Count != 1 ? "s" : "")}";
            AllReviewed = unreviewed.Count == 0;

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
    private async Task CopyClaudeContextAsync()
    {
        try
        {
            // Build a context string from recent games, objectives, and review focus
            var recent = await _gameRepo.GetRecentAsync(limit: 5);
            var focus = await _gameRepo.GetLastReviewFocusAsync();
            var objectives = await _objectivesRepo.GetActiveAsync();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== LoL Review Context ===");
            sb.AppendLine();

            if (focus != null && !string.IsNullOrWhiteSpace(focus.FocusNext))
            {
                sb.AppendLine($"Focus for next game: {focus.FocusNext}");
                sb.AppendLine();
            }

            if (objectives.Count > 0)
            {
                sb.AppendLine("Active Objectives:");
                foreach (var obj in objectives)
                {
                    var title = obj.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "";
                    sb.AppendLine($"  - {title}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("Recent Games:");
            foreach (var game in recent)
            {
                var wl = game.Win ? "W" : "L";
                sb.AppendLine($"  {wl} {game.ChampionName} {game.Kills}/{game.Deaths}/{game.Assists} ({game.KdaRatio:F1} KDA)");
            }

            var context = sb.ToString();

            DispatcherHelper.RunOnUIThread(() =>
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(context);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            });

            ClaudeButtonText = "Copied!";
            await Task.Delay(1500);
            ClaudeButtonText = "Copy Claude Context";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy Claude context");
            ClaudeButtonText = "Error";
            await Task.Delay(1500);
            ClaudeButtonText = "Copy Claude Context";
        }
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
            GameMode = game.GameMode,
            WinLossColorHex = game.Win ? "#22c55e" : "#ef4444",
            BorderColorHex = game.Win ? "#22c55e" : "#ef4444",
            HasReview = !string.IsNullOrWhiteSpace(game.RawStats.TryGetValue("mistakes", out var m) ? m?.ToString() : null)
                     || !string.IsNullOrWhiteSpace(game.RawStats.TryGetValue("went_well", out var w) ? w?.ToString() : null),
            DamageText = FormatNumber(game.TotalDamageToChampions),
            StatsLine = $"CS {game.CsTotal} ({game.CsPerMin:F1}/m)  \u2022  Vision {game.VisionScore}  \u2022  {FormatNumber(game.TotalDamageToChampions)} dmg"
        };
    }

    private static string FormatNumber(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString()
    };

    private static string GetLevelColor(int levelIndex) => levelIndex switch
    {
        0 => "#6b7280",  // Exploring: Gray
        1 => "#3b82f6",  // Drilling: Blue
        2 => "#8b5cf6",  // Ingraining: Purple
        3 => "#c89b3c",  // Ready: Gold
        _ => "#6b7280"
    };
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
    public string WinLossColorHex { get; set; } = "#e8e8f0";
    public string BorderColorHex { get; set; } = "#1e1e2e";
    public bool HasReview { get; set; }
    public bool HasVod { get; set; }
    public string DamageText { get; set; } = "";
    public string StatsLine { get; set; } = "";
}

/// <summary>Flattened objective data for display binding on the dashboard.</summary>
public class DashboardObjectiveItem
{
    public string Title { get; set; } = "";
    public string LevelName { get; set; } = "";
    public int Score { get; set; }
    public int GameCount { get; set; }
    public double Progress { get; set; }
    public string LevelColorHex { get; set; } = "#7070a0";
    public string InfoText { get; set; } = "";
}

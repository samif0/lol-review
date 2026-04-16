#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using Microsoft.Extensions.Logging;
using System.IO;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the History page — browse past games, stats, champion breakdown.</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly IVodRepository _vodRepo;
    private readonly INavigationService _navigationService;
    private readonly ILogger<HistoryViewModel> _logger;

    private const int PageSize = 20;

    // ── Observable Properties ───────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _currentPage;

    [ObservableProperty]
    private bool _hasMorePages;

    [ObservableProperty]
    private bool _hasNoGames;

    // ── Stats Overview properties ───────────────────────────────────

    [ObservableProperty]
    private string _overallWinRateText = "0.0%";

    [ObservableProperty]
    private string _overallWinRateColorHex = "#e8e8f0";

    [ObservableProperty]
    private string _overallRecordText = "0W 0L (0 games)";

    [ObservableProperty]
    private string _avgKillsText = "0.0";

    [ObservableProperty]
    private string _avgDeathsText = "0.0";

    [ObservableProperty]
    private string _avgAssistsText = "0.0";

    [ObservableProperty]
    private string _avgKdaText = "0.00";

    [ObservableProperty]
    private string _avgCsMinText = "0.0";

    [ObservableProperty]
    private string _avgVisionText = "0.0";

    [ObservableProperty]
    private string _bestKdaText = "0.0";

    [ObservableProperty]
    private string _maxKillsText = "0";

    [ObservableProperty]
    private string _pentasText = "0";

    [ObservableProperty]
    private string _quadrasText = "0";

    [ObservableProperty]
    private bool _hasStats;

    // ── Champion filter ─────────────────────────────────────────────

    [ObservableProperty]
    private string _selectedChampionFilter = "All Champions";

    [ObservableProperty]
    private int _selectedWinLossFilter; // 0=All, 1=Wins, 2=Losses

    // ── Collections ─────────────────────────────────────────────────

    public ObservableCollection<GameDisplayItem> Games { get; } = new();
    public ObservableCollection<ChampionStatsDisplayItem> ChampionStats { get; } = new();
    public ObservableCollection<string> ChampionFilters { get; } = new() { "All Champions" };

    // ── Constructor ─────────────────────────────────────────────────

    public HistoryViewModel(
        IGameRepository gameRepo,
        IVodRepository vodRepo,
        INavigationService navigationService,
        ILogger<HistoryViewModel> logger)
    {
        _gameRepo = gameRepo;
        _vodRepo = vodRepo;
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
            CurrentPage = 0;
            DispatcherHelper.RunOnUIThread(() => Games.Clear());

            await LoadGamesPageAsync();
            await LoadChampionFiltersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load history data");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || !HasMorePages) return;
        IsLoading = true;

        try
        {
            CurrentPage++;
            await LoadGamesPageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load more games");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task FilterChangedAsync()
    {
        CurrentPage = 0;
        DispatcherHelper.RunOnUIThread(() => Games.Clear());
        IsLoading = true;
        try
        {
            await LoadGamesPageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply filter");
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
    private void NavigateToVodPlayer(long gameId)
    {
        _navigationService.NavigateTo("vodplayer", gameId);
    }

    [RelayCommand]
    private async Task HideGameAsync(long gameId)
    {
        await _gameRepo.SetHiddenAsync(gameId, hidden: true);
        // Remove from current list immediately without a full reload
        var item = Games.FirstOrDefault(g => g.GameId == gameId);
        if (item is not null)
            Games.Remove(item);
    }

    // ── Private load methods ────────────────────────────────────────

    private async Task LoadGamesPageAsync()
    {
        var offset = CurrentPage * PageSize;
        bool? selectedWin = SelectedWinLossFilter switch
        {
            1 => true,
            2 => false,
            _ => null
        };

        var gamesTask = _gameRepo.GetRecentAsync(
            limit: PageSize,
            offset: offset,
            champion: SelectedChampionFilter,
            win: selectedWin);
        var totalCountTask = _gameRepo.GetRecentCountAsync(
            champion: SelectedChampionFilter,
            win: selectedWin);

        await Task.WhenAll(gamesTask, totalCountTask);

        var games = gamesTask.Result;
        var totalCount = totalCountTask.Result;

        HasMorePages = offset + games.Count < totalCount;
        HasNoGames = Games.Count == 0 && totalCount == 0;

        var displayItems = games.Select(MapGameDisplay).ToList();
        var vodPaths = await _vodRepo.GetVodPathsAsync(displayItems.Select(g => g.GameId).ToArray());
        foreach (var item in displayItems)
        {
            if (vodPaths.TryGetValue(item.GameId, out var path) && File.Exists(path))
            {
                item.HasVod = true;
            }
        }

        DispatcherHelper.RunOnUIThread(() =>
        {
            foreach (var item in displayItems)
            {
                Games.Add(item);
            }
        });
    }

    private async Task LoadStatsOverviewAsync()
    {
        var overall = await _gameRepo.GetOverallStatsAsync();

        if (overall.TotalGames == 0)
        {
            HasStats = false;
            return;
        }

        HasStats = true;
        var losses = overall.TotalGames - overall.TotalWins;

        OverallWinRateText = $"{overall.Winrate:F1}%";
        OverallWinRateColorHex = overall.Winrate >= 50 ? "#7EC9A0" : "#D38C90";
        OverallRecordText = $"Win Rate  \u2022  {overall.TotalWins}W {losses}L ({overall.TotalGames} games)";

        AvgKillsText = $"{overall.AvgKills:F1}";
        AvgDeathsText = $"{overall.AvgDeaths:F1}";
        AvgAssistsText = $"{overall.AvgAssists:F1}";
        AvgKdaText = $"{overall.AvgKda:F2}";
        AvgCsMinText = $"{overall.AvgCsMin:F1}";
        AvgVisionText = $"{overall.AvgVision:F1}";
        BestKdaText = $"{overall.BestKda:F1}";
        MaxKillsText = $"{overall.MaxKills}";
        PentasText = $"{overall.TotalPentas}";
        QuadrasText = $"{overall.TotalQuadras}";
    }

    private async Task LoadChampionStatsAsync()
    {
        var champStats = await _gameRepo.GetChampionStatsAsync();

        DispatcherHelper.RunOnUIThread(() =>
        {
            ChampionStats.Clear();
            foreach (var champ in champStats)
            {
                ChampionStats.Add(new ChampionStatsDisplayItem
                {
                    ChampionName = champ.ChampionName,
                    GamesPlayed = champ.GamesPlayed,
                    GamesText = champ.GamesPlayed.ToString(),
                    WinRateText = $"{champ.Winrate:F1}%",
                    WinRateColorHex = champ.Winrate >= 50 ? "#7EC9A0" : "#D38C90",
                    AvgKdaText = $"{champ.AvgKda:F2}",
                    AvgCsMinText = $"{champ.AvgCsMin:F1}",
                    AvgDamageText = FormatNumber((int)champ.AvgDamage)
                });
            }
        });
    }

    private async Task LoadChampionFiltersAsync()
    {
        var champions = await _gameRepo.GetUniqueChampionsAsync();

        DispatcherHelper.RunOnUIThread(() =>
        {
            ChampionFilters.Clear();
            ChampionFilters.Add("All Champions");
            foreach (var champ in champions.OrderBy(c => c))
            {
                ChampionFilters.Add(champ);
            }
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static GameDisplayItem MapGameDisplay(Core.Models.GameStats game)
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
            KdaRatioText = $"({game.KdaRatio:F1} KDA)",
            CsTotal = game.CsTotal,
            CsPerMin = game.CsPerMin,
            VisionScore = game.VisionScore,
            TotalDamageToChampions = game.TotalDamageToChampions,
            Duration = duration,
            DatePlayed = date,
            GameMode = game.DisplayGameMode,
            WinLossColorHex = game.Win ? "#7EC9A0" : "#D38C90",
            BorderColorHex = game.Win ? "#7EC9A0" : "#D38C90",
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
}

// ── Display models ──────────────────────────────────────────────────

/// <summary>Flattened champion stats for display binding.</summary>
public class ChampionStatsDisplayItem
{
    public string ChampionName { get; set; } = "";
    public int GamesPlayed { get; set; }
    public string GamesText { get; set; } = "0";
    public string WinRateText { get; set; } = "0%";
    public string WinRateColorHex { get; set; } = "#e8e8f0";
    public string AvgKdaText { get; set; } = "0.00";
    public string AvgCsMinText { get; set; } = "0.0";
    public string AvgDamageText { get; set; } = "0";
}

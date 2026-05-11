#nullable enable

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Services;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.App.ViewModels;

public enum GamesWorkspaceView
{
    Queue,
    Today,
    History,
    Vod,
}

/// <summary>Consolidated game workspace for review queue, session, history, and VOD discovery.</summary>
public partial class GamesViewModel : ObservableObject
{
    private const int HistoryPageSize = 30;

    private readonly IGameHistoryQuery _gameHistory;
    private readonly IGameDeletionService _gameDeletion;
    private readonly IVodRepository _vodRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly IConfigService _configService;
    private readonly IReviewWorkflowService _reviewWorkflow;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly ILogger<GamesViewModel> _logger;

    private int _historyPage;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _selectedViewIndex;
    [ObservableProperty] private string _heading = "Review Queue";
    [ObservableProperty] private string _emptyMessage = "No games need review.";
    [ObservableProperty] private bool _hasGames;
    [ObservableProperty] private bool _hasMoreHistory;

    public ObservableCollection<GameDisplayItem> Games { get; } = new();

    public GamesViewModel(
        IGameHistoryQuery gameHistory,
        IGameDeletionService gameDeletion,
        IVodRepository vodRepo,
        IObjectivesRepository objectivesRepo,
        INavigationService navigationService,
        IDialogService dialogService,
        IConfigService configService,
        IReviewWorkflowService reviewWorkflow,
        ISessionLogRepository sessionLogRepo,
        ILogger<GamesViewModel> logger)
    {
        _gameHistory = gameHistory;
        _gameDeletion = gameDeletion;
        _vodRepo = vodRepo;
        _objectivesRepo = objectivesRepo;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _configService = configService;
        _reviewWorkflow = reviewWorkflow;
        _sessionLogRepo = sessionLogRepo;
        _logger = logger;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        _historyPage = 0;
        await LoadSelectedViewAsync(append: false);
    }

    [RelayCommand]
    private async Task SwitchViewAsync(int index)
    {
        if (index < 0 || index > 3) return;
        SelectedViewIndex = index;
        _historyPage = 0;
        await LoadSelectedViewAsync(append: false);
    }

    [RelayCommand]
    private async Task LoadMoreHistoryAsync()
    {
        if (CurrentView != GamesWorkspaceView.History || IsLoading || !HasMoreHistory) return;
        _historyPage++;
        await LoadSelectedViewAsync(append: true);
    }

    [RelayCommand]
    private void PrimaryAction(long gameId)
    {
        var item = Games.FirstOrDefault(g => g.GameId == gameId);
        if (item?.HasReview == true && item.HasVod)
        {
            _navigationService.NavigateTo("vodplayer", gameId);
            return;
        }

        _navigationService.NavigateTo("review", gameId);
    }

    [RelayCommand]
    private async Task SkipReviewAsync(long gameId)
    {
        var game = await _gameHistory.GetAsync(gameId);
        if (game is null) return;

        var result = await _reviewWorkflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: game.ChampionName,
            Win: game.Win,
            RequireReviewNotes: false,
            Snapshot: BuildEmptySnapshot(game.EnemyLaner)));
        if (!result.Success) return;

        await _sessionLogRepo.MarkSkippedAsync(gameId);
        WeakReferenceMessenger.Default.Send(new GameReviewedMessage(gameId));
        await LoadSelectedViewAsync(append: false);
    }

    [RelayCommand]
    private async Task DeleteGameAsync(long gameId)
    {
        var game = Games.FirstOrDefault(g => g.GameId == gameId);
        var label = game is null
            ? "this game"
            : $"{game.ChampionName} ({(game.Win ? "W" : "L")})";

        var confirmed = await _dialogService.ShowConfirmAsync(
            $"Delete {label}?",
            "This permanently removes the game, its review, and all practice tracking. " +
            "A database backup is saved first.");
        if (!confirmed) return;

        try
        {
            await _gameDeletion.DeleteAsync(gameId);
            if (game is not null) Games.Remove(game);
            HasGames = Games.Count > 0;
            WeakReferenceMessenger.Default.Send(new GameDeletedMessage(gameId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete game {GameId}", gameId);
        }
    }

    private GamesWorkspaceView CurrentView => (GamesWorkspaceView)Math.Clamp(SelectedViewIndex, 0, 3);

    private async Task LoadSelectedViewAsync(bool append)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var games = CurrentView switch
            {
                GamesWorkspaceView.Queue => await _gameHistory.GetUnreviewedGamesAsync(days: 14),
                GamesWorkspaceView.Today => await _gameHistory.GetTodaysGamesAsync(),
                GamesWorkspaceView.History => await LoadHistoryPageAsync(),
                GamesWorkspaceView.Vod => await LoadVodGamesAsync(),
                _ => []
            };

            var items = games.Select(MapGameDisplay).ToList();
            await EnrichRowsAsync(items);
            ApplyViewCopy();

            DispatcherHelper.RunOnUIThread(() =>
            {
                if (!append) Games.Clear();
                foreach (var item in items) Games.Add(item);
                HasGames = Games.Count > 0;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Games workspace view {View}", CurrentView);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<List<GameStats>> LoadHistoryPageAsync()
    {
        var offset = _historyPage * HistoryPageSize;
        var gamesTask = _gameHistory.GetRecentAsync(limit: HistoryPageSize, offset: offset);
        var countTask = _gameHistory.GetRecentCountAsync();
        await Task.WhenAll(gamesTask, countTask);
        HasMoreHistory = offset + gamesTask.Result.Count < countTask.Result;
        return gamesTask.Result;
    }

    private async Task<List<GameStats>> LoadVodGamesAsync()
    {
        var recent = await _gameHistory.GetRecentAsync(limit: 120, offset: 0);
        var vodPaths = await _vodRepo.GetVodPathsAsync(recent.Select(g => g.GameId).ToArray());
        HasMoreHistory = false;
        return recent
            .Where(g => vodPaths.TryGetValue(g.GameId, out var path) && File.Exists(path))
            .ToList();
    }

    private async Task EnrichRowsAsync(List<GameDisplayItem> items)
    {
        var vodPaths = await _vodRepo.GetVodPathsAsync(items.Select(g => g.GameId).ToArray());
        foreach (var item in items)
        {
            item.HasVod = vodPaths.TryGetValue(item.GameId, out var path) && File.Exists(path);

            var objectives = await _objectivesRepo.GetGameObjectivesAsync(item.GameId);
            var bookmarks = item.HasVod
                ? await _vodRepo.GetBookmarksAsync(item.GameId)
                : [];
            var hasObjectiveEvidence = bookmarks.Any(b => b.ObjectiveId is not null);
            var practiced = objectives.Any(o => o.Practiced);

            item.ObjectiveStateText = hasObjectiveEvidence
                ? "Evidence tagged"
                : practiced && item.HasVod && item.HasReview
                    ? "VOD evidence pending"
                    : practiced
                        ? "Objective practiced"
                        : "No objective tag";
            item.ReviewStateText = item.HasReview ? "Reviewed" : "Unreviewed";
            item.VodStateText = item.HasVod ? "VOD linked" : "No VOD";
            item.PrimaryActionText = !item.HasReview
                ? "Review"
                : item.HasVod
                    ? "Review VOD"
                    : "Open";
            item.StatsLine = $"{item.ReviewStateText}  •  {item.VodStateText}  •  {item.ObjectiveStateText}";
        }
    }

    private void ApplyViewCopy()
    {
        HasMoreHistory = CurrentView == GamesWorkspaceView.History && HasMoreHistory;
        Heading = CurrentView switch
        {
            GamesWorkspaceView.Queue => "Review Queue",
            GamesWorkspaceView.Today => "Today",
            GamesWorkspaceView.History => "History",
            GamesWorkspaceView.Vod => "VOD Review",
            _ => "Games"
        };
        EmptyMessage = CurrentView switch
        {
            GamesWorkspaceView.Queue => "No games need review.",
            GamesWorkspaceView.Today => "No games logged today.",
            GamesWorkspaceView.History => "No games recorded yet.",
            GamesWorkspaceView.Vod => "No VOD recordings are linked yet.",
            _ => "No games found."
        };
    }

    private GameDisplayItem MapGameDisplay(GameStats game)
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
            EnemyChampion = game.EnemyLaner,
            ParticipantMapJson = game.ParticipantMap,
            GameRole = string.IsNullOrWhiteSpace(game.Position) ? _configService.PrimaryRole : game.Position,
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
            HasReview = HasPersistedReview(game),
            DamageText = FormatNumber(game.TotalDamageToChampions),
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

    private static ReviewSnapshot BuildEmptySnapshot(string enemyLaner) => new(
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
        EnemyLaner: enemyLaner,
        MatchupNote: "",
        SelectedTagIds: Array.Empty<long>(),
        ObjectivePractices: Array.Empty<SaveObjectivePracticeRequest>());

    private static string FormatNumber(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString()
    };
}

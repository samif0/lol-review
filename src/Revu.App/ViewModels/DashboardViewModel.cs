#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Services;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;
using Windows.Storage.Pickers;

namespace Revu.App.ViewModels;

/// <summary>
/// The four Dashboard stages. Drives which sections render — new users
/// see only the Next Step card, veterans see the full stat strip + queue
/// + objectives grid.
/// </summary>
public enum DashboardStage
{
    /// <summary>0 games captured — first-launch state.</summary>
    NoGames,

    /// <summary>1-2 games captured, at least one is unreviewed.</summary>
    HasUnreviewed,

    /// <summary>3+ reviewed games, no active objectives yet.</summary>
    NeedsObjective,

    /// <summary>5+ reviewed games — power user, full dashboard visible.</summary>
    Normal,
}

/// <summary>ViewModel for the Dashboard page — session overview, stats, unreviewed games.</summary>
public partial class DashboardViewModel : ObservableObject
{
    // Tunable — number of reviewed games before the stat strip + objectives grid
    // show up. Below this, averages are noisy and streaks feel unearned.
    private const int NormalStageThreshold = 5;

    // At or above this many *reviewed* games (but below NormalStageThreshold),
    // if there are no active objectives, nudge toward setting one.
    private const int NeedsObjectiveThreshold = 3;

    private readonly IGameRepository _gameRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly INavigationService _navigationService;
    private readonly IConfigService _configService;
    private readonly IDialogService _dialogService;
    private readonly IAnalysisService _analysisService;
    private readonly ILogger<DashboardViewModel> _logger;

    // ── Observable Properties ───────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _totalGames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WinratePercent))]
    [NotifyPropertyChangedFor(nameof(RecordLine))]
    private int _wins;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WinratePercent))]
    [NotifyPropertyChangedFor(nameof(RecordLine))]
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

    // v2.15.0: LastFocus removed from the Dashboard. The underlying focus_next
    // field is no longer written to by the slimmed-down Review flow; relying
    // on it here would produce an increasingly empty card over time.
    //
    // PreGamePage still reads it (that's fine — it degrades to blank as old
    // games fall out of the user's recency window).

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

    // ── Empty-state (stage + next step + ascent reminder) ──────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMainDashboardSections))]
    [NotifyPropertyChangedFor(nameof(ShowNextStepCard))]
    [NotifyPropertyChangedFor(nameof(NextStepCardBorderHex))]
    [NotifyPropertyChangedFor(nameof(ShowNextStepCta))]
    [NotifyPropertyChangedFor(nameof(ShowNextStepDismiss))]
    private DashboardStage _stage = DashboardStage.NoGames;

    [ObservableProperty]
    private string _nextStepEyebrow = "";

    [ObservableProperty]
    private string _nextStepTitle = "";

    [ObservableProperty]
    private string _nextStepBody = "";

    [ObservableProperty]
    private string _nextStepCta = "";

    [ObservableProperty]
    private bool _showAscentReminder;

    /// <summary>True when the Normal-stage sections (stat strip + queue + objectives) should render.</summary>
    public bool ShowMainDashboardSections => Stage == DashboardStage.Normal;

    /// <summary>True when the Next Step card should render (anything other than Normal).</summary>
    public bool ShowNextStepCard => Stage != DashboardStage.Normal;

    /// <summary>Stage-specific border color — blue for the hero stages, violet for the objective nudge.</summary>
    public string NextStepCardBorderHex => Stage switch
    {
        DashboardStage.NeedsObjective => "#8A7CFF", // AccentPurple
        _ => "#6EC8D7",                             // AccentTeal — reads as "onboarding"
    };

    /// <summary>Whether the Next Step card should show its CTA button (NoGames is message-only).</summary>
    public bool ShowNextStepCta => Stage is DashboardStage.HasUnreviewed or DashboardStage.NeedsObjective;

    /// <summary>
    /// Whether the Next Step card should show a dismiss control. Lowest-data
    /// states (NoGames, HasUnreviewed) have none — the user needs the prompt.
    /// Only the NeedsObjective nudge is dismissible.
    /// </summary>
    public bool ShowNextStepDismiss => Stage == DashboardStage.NeedsObjective;

    public ObservableCollection<GameDisplayItem> TodaysGames { get; } = new();
    public ObservableCollection<GameDisplayItem> UnreviewedGames { get; } = new();
    public ObservableCollection<DashboardObjectiveItem> ActiveObjectives { get; } = new();

    /// <summary>Winrate display string, e.g. "60%" — empty when there are no games yet.</summary>
    public string WinratePercent
    {
        get
        {
            var games = Wins + Losses;
            if (games == 0) return "—";
            return $"{(int)Math.Round(100.0 * Wins / games)}%";
        }
    }

    /// <summary>"3W // 2L" compact win/loss line. Empty when there are no games yet.</summary>
    public string RecordLine => (Wins + Losses) == 0 ? "" : $"{Wins}W // {Losses}L";

    // Cached suggestion produced during LoadAsync, so the "SET OBJECTIVE"
    // button can pre-fill the create form without re-running the full
    // profile generation when clicked.
    private ObjectiveSuggestion? _cachedFirstSuggestion;

    // If the user dismisses the "set your first objective" nudge in the
    // current session, don't re-show it on reload. Resets on app relaunch.
    private bool _needsObjectiveDismissedForSession;

    // ── Constructor ─────────────────────────────────────────────────

    public DashboardViewModel(
        IGameRepository gameRepo,
        ISessionLogRepository sessionLogRepo,
        IObjectivesRepository objectivesRepo,
        INavigationService navigationService,
        IConfigService configService,
        IDialogService dialogService,
        IAnalysisService analysisService,
        ILogger<DashboardViewModel> logger)
    {
        _gameRepo = gameRepo;
        _sessionLogRepo = sessionLogRepo;
        _objectivesRepo = objectivesRepo;
        _navigationService = navigationService;
        _configService = configService;
        _dialogService = dialogService;
        _analysisService = analysisService;
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
            // Build greeting. Prefer the Riot gameName (e.g. "chapy") over the email
            // since it's what the user sees in-client. Falls back to the generic
            // "lock in." tail when the user isn't logged in or hasn't linked an ID.
            var hour = DateTime.Now.Hour;
            var tod = hour < 12 ? "morning" : hour < 17 ? "afternoon" : "evening";
            Greeting = BuildGreeting(tod);

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

            // v2.15.0: LastFocus removed — see comment on the ex-observables above.

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
                        LevelDimColorHex = AppSemanticPalette.ObjectiveLevelDimHex(info.LevelIndex),
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

            // Decide the onboarding/empty-state stage *after* everything else
            // loaded, using the queries we already have plus overall totals.
            await ComputeStageAsync(activeObjectiveCount: objectives.Count);

            // Refresh the Ascent reminder from config each load — user may
            // have picked a folder in Settings since we last looked.
            RefreshAscentReminder();
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

    private string BuildGreeting(string tod)
    {
        var id = _configService.RiotId;
        var hashPos = id.IndexOf('#');
        var gameName = hashPos > 0 ? id.Substring(0, hashPos) : "";

        if (_configService.HasValidRiotSession && !string.IsNullOrEmpty(gameName))
        {
            return $"Good {tod}, {gameName}.";
        }
        if (_configService.HasValidRiotSession)
        {
            return $"Good {tod} \u2014 link your Riot account.";
        }
        return $"Good {tod} \u2014 lock in.";
    }

    [RelayCommand]
    private void RunReset()
    {
        // Voluntary tilt check — no tied-to-game-id context.
        _navigationService.NavigateTo("tiltcheck", new TiltCheckInfo(GameId: null, StreakCount: 0));
    }

    [RelayCommand]
    private async Task DeleteGameAsync(long gameId)
    {
        var game = UnreviewedGames.FirstOrDefault(g => g.GameId == gameId)
                   ?? TodaysGames.FirstOrDefault(g => g.GameId == gameId);
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

            var unreviewedItem = UnreviewedGames.FirstOrDefault(g => g.GameId == gameId);
            if (unreviewedItem is not null) UnreviewedGames.Remove(unreviewedItem);
            var todayItem = TodaysGames.FirstOrDefault(g => g.GameId == gameId);
            if (todayItem is not null) TodaysGames.Remove(todayItem);
            UpdateUnreviewedSummary(UnreviewedGames.Count);

            WeakReferenceMessenger.Default.Send(new GameDeletedMessage(gameId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete game {GameId}", gameId);
            await _dialogService.ShowConfirmAsync(
                "Delete failed",
                "Couldn't delete the game. Your database backup is still safe on disk.");
        }
    }

    // ── Empty-state / next-step commands ────────────────────────────

    /// <summary>
    /// Primary action on the Next Step card — branches on <see cref="Stage"/>.
    /// No-op for NoGames (card is message-only) and Normal (card is hidden).
    /// </summary>
    [RelayCommand]
    private async Task TakeNextStepAsync()
    {
        switch (Stage)
        {
            case DashboardStage.HasUnreviewed:
                // Jump to the first unreviewed game if we have one, otherwise
                // fall back to the session logger.
                var first = UnreviewedGames.FirstOrDefault();
                if (first is not null)
                {
                    _navigationService.NavigateTo("review", first.GameId);
                }
                else
                {
                    _navigationService.NavigateTo("session");
                }
                break;

            case DashboardStage.NeedsObjective:
                await NavigateToObjectivesWithSeedAsync();
                break;

            default:
                // NoGames + Normal have no button wired to this command.
                break;
        }
    }

    /// <summary>
    /// Navigate to Objectives with a pre-seeded suggestion (if we have one)
    /// so the create form opens pre-filled with the top AI-rule match.
    /// </summary>
    private async Task NavigateToObjectivesWithSeedAsync()
    {
        // Re-run the suggestion query if the cache is empty (e.g. user hit
        // the button without a prior LoadAsync populating it).
        if (_cachedFirstSuggestion is null)
        {
            try
            {
                var profile = await _analysisService.GenerateProfileAsync();
                var list = _analysisService.GenerateSuggestions(profile, limit: 1);
                _cachedFirstSuggestion = list.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dashboard: on-demand suggestion generation failed");
            }
        }

        // Pass the suggestion (or null) through — ObjectivesPage opens its
        // create form regardless, just without pre-fill if null.
        _navigationService.NavigateTo("objectives", _cachedFirstSuggestion);
    }

    /// <summary>
    /// Dismiss the "set your first objective" nudge for the rest of the
    /// current app session. Resets on relaunch.
    /// </summary>
    [RelayCommand]
    private void DismissNextStep()
    {
        if (Stage != DashboardStage.NeedsObjective) return;
        _needsObjectiveDismissedForSession = true;
        // Promoting to Normal bypasses the card without hiding the rest.
        Stage = DashboardStage.Normal;
        PopulateNextStepCopy();
    }

    /// <summary>Open the folder picker to set the Ascent recordings folder.</summary>
    [RelayCommand]
    private async Task PickAscentFolderAsync()
    {
        var folder = await PickFolderAsync("Select Ascent Recordings Folder");
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            var config = await _configService.LoadAsync();
            config.AscentFolder = folder!;
            await _configService.SaveAsync(config);
            RefreshAscentReminder();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Ascent folder from dashboard reminder");
        }
    }

    /// <summary>
    /// Permanently dismiss the Ascent reminder card (persists in config).
    /// The user can still set the folder later via Settings.
    /// </summary>
    [RelayCommand]
    private async Task DismissAscentReminderAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            config.AscentReminderDismissed = true;
            await _configService.SaveAsync(config);
            RefreshAscentReminder();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist Ascent-reminder dismiss");
        }
    }

    // ── Stage computation ───────────────────────────────────────────

    private async Task ComputeStageAsync(int activeObjectiveCount)
    {
        try
        {
            // Total captured games (all-time, ranked/normal, is_hidden=0).
            var overall = await _gameRepo.GetOverallStatsAsync();
            var totalCaptured = overall.TotalGames;

            if (totalCaptured == 0)
            {
                Stage = DashboardStage.NoGames;
                _cachedFirstSuggestion = null;
                PopulateNextStepCopy();
                return;
            }

            // Count reviewed games — sample the recent 50 and run the same
            // HasPersistedReview heuristic we use on the Unreviewed queue.
            // 50 is enough to distinguish 0/1/2/3/5+ since the stage
            // thresholds are all well under that.
            var recent = await _gameRepo.GetRecentAsync(limit: 50, offset: 0);
            var reviewedCount = recent.Count(HasPersistedReview);

            if (reviewedCount >= NormalStageThreshold)
            {
                Stage = DashboardStage.Normal;
                _cachedFirstSuggestion = null;
            }
            else if (reviewedCount >= NeedsObjectiveThreshold && activeObjectiveCount == 0)
            {
                if (_needsObjectiveDismissedForSession)
                {
                    // User opted out for this session — don't nag again.
                    Stage = DashboardStage.Normal;
                    _cachedFirstSuggestion = null;
                }
                else
                {
                    Stage = DashboardStage.NeedsObjective;
                    // Pre-warm the suggestion so the CTA click feels instant.
                    try
                    {
                        var profile = await _analysisService.GenerateProfileAsync();
                        var list = _analysisService.GenerateSuggestions(profile, limit: 1);
                        _cachedFirstSuggestion = list.FirstOrDefault();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Dashboard: suggestion pre-warm failed");
                        _cachedFirstSuggestion = null;
                    }
                }
            }
            else
            {
                // 1-2 captured games, or (3-4 reviewed AND active objective already exists).
                // Both paths land on HasUnreviewed: the unreviewed queue or
                // the natural play-more-games prompt.
                Stage = DashboardStage.HasUnreviewed;
                _cachedFirstSuggestion = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute Dashboard stage");
            // Default conservatively to Normal so the user always sees *something*.
            Stage = DashboardStage.Normal;
            _cachedFirstSuggestion = null;
        }

        PopulateNextStepCopy();
    }

    private void PopulateNextStepCopy()
    {
        switch (Stage)
        {
            case DashboardStage.NoGames:
                NextStepEyebrow = "START HERE";
                NextStepTitle = "Play a ranked game";
                NextStepBody = "Revu captures it automatically once the game ends. "
                             + "Leave the app running in the background while you queue.";
                NextStepCta = "";
                break;

            case DashboardStage.HasUnreviewed:
                NextStepEyebrow = "NEXT STEP";
                NextStepTitle = "Your last game is ready to review";
                NextStepBody = "Write a few sentences while it's fresh — what went well, the biggest mistake, "
                             + "and one thing to focus on next. Consistency beats length.";
                NextStepCta = "REVIEW NOW";
                break;

            case DashboardStage.NeedsObjective:
                NextStepEyebrow = "NEXT STEP";
                NextStepTitle = "Set your first objective";
                NextStepBody = _cachedFirstSuggestion is not null
                    ? $"Based on your reviews so far: \u201C{_cachedFirstSuggestion.Title}\u201D. "
                      + "Try it as a practice focus for your next few games."
                    : "Pick one specific thing you want to practice over your next few games. "
                      + "Objectives stack score as you keep at them.";
                NextStepCta = "SET OBJECTIVE";
                break;

            default:
                NextStepEyebrow = "";
                NextStepTitle = "";
                NextStepBody = "";
                NextStepCta = "";
                break;
        }
    }

    private void RefreshAscentReminder()
    {
        // Show when the user has never pointed at a folder AND hasn't told
        // us to stop nagging. IsAscentEnabled returns false on both "empty
        // config" and "folder configured but missing on disk" — the latter
        // is still worth a reminder.
        var dismissed = _configService.AscentReminderDismissed;
        ShowAscentReminder = !dismissed && !_configService.IsAscentEnabled;
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
            EnemyChampion = game.EnemyLaner,
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

    // ── Folder picker helper ────────────────────────────────────────
    //
    // Mirrors SettingsViewModel.PickFolderAsync — WinUI 3 FolderPicker needs
    // the owning window HWND wired via InitializeWithWindow or it throws.
    private static async Task<string?> PickFolderAsync(string description)
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            picker.FileTypeFilter.Add("*");

            var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == nint.Zero) return null;

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch
        {
            return null;
        }
    }
}

// ── Display models ──────────────────────────────────────────────────

/// <summary>Flattened game data for display binding in the UI.</summary>
public class GameDisplayItem
{
    public long GameId { get; set; }
    public string ChampionName { get; set; } = "";
    public string EnemyChampion { get; set; } = "";
    public bool Win { get; set; }

    /// <summary>"Kai'Sa vs Tristana" when enemy known, otherwise just "Kai'Sa".
    /// Used by GameRowCard.Champion so games-list pills identify the matchup.</summary>
    public string ChampionDisplay => string.IsNullOrWhiteSpace(EnemyChampion)
        ? ChampionName
        : $"{ChampionName} vs {EnemyChampion}";
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

    /// <summary>"GAMEMODE // DATE // DURATION" for the GameRowCard meta line.</summary>
    public string MetaLine
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(GameMode)) parts.Add(GameMode.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(DatePlayed)) parts.Add(DatePlayed.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(Duration)) parts.Add(Duration);
            return string.Join("  //  ", parts);
        }
    }
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
    public string LevelDimColorHex { get; set; } = "#10121A";
    public string InfoText { get; set; } = "";

    /// <summary>Short percentage label for the center of HudProgressRing.</summary>
    public string ProgressLabel => $"{Math.Clamp((int)Math.Round(Progress * 100.0), 0, 100)}%";

    /// <summary>"LVL N // PHASE // SCORE PTS" for the meta line beside the ring.</summary>
    public string MetaText => string.IsNullOrWhiteSpace(LevelName)
        ? PhaseLabel.ToUpperInvariant()
        : $"{LevelName.ToUpperInvariant()}  //  {PhaseLabel.ToUpperInvariant()}  //  {Score} PTS";
    public Microsoft.UI.Xaml.Media.SolidColorBrush LevelColorBrush =>
        AppSemanticPalette.Brush(LevelColorHex);

    public Microsoft.UI.Xaml.Media.SolidColorBrush LevelDimColorBrush =>
        AppSemanticPalette.Brush(LevelDimColorHex);
}

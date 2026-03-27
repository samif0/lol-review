#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the inline game review page.</summary>
public partial class ReviewViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly IConceptTagRepository _conceptTagRepo;
    private readonly IVodRepository _vodRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly INavigationService _navigationService;
    private readonly ILogger<ReviewViewModel> _logger;

    // ── Game Data ───────────────────────────────────────────────────

    [ObservableProperty]
    private long _gameId;

    [ObservableProperty]
    private string _championName = "";

    [ObservableProperty]
    private bool _win;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private string _resultColorHex = "#e8e8f0";

    [ObservableProperty]
    private string _kdaText = "";

    [ObservableProperty]
    private string _kdaRatioText = "";

    [ObservableProperty]
    private string _gameModeText = "";

    [ObservableProperty]
    private string _durationText = "";

    [ObservableProperty]
    private string _headerText = "Review";

    // ── Stat display properties ─────────────────────────────────────

    [ObservableProperty]
    private string _damageText = "";

    [ObservableProperty]
    private string _csText = "";

    [ObservableProperty]
    private string _csPerMinText = "";

    [ObservableProperty]
    private string _visionText = "";

    [ObservableProperty]
    private string _goldText = "";

    [ObservableProperty]
    private string _killParticipationText = "";

    [ObservableProperty]
    private string _damageTakenText = "";

    [ObservableProperty]
    private string _wardsPlacedText = "";

    // ── VOD ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _hasVod;

    [ObservableProperty]
    private int _bookmarkCount;

    // ── Review Fields ───────────────────────────────────────────────

    [ObservableProperty]
    private int _mentalRating = 5;

    [ObservableProperty]
    private string _mentalRatingColorHex = "#0099ff";

    [ObservableProperty]
    private string _wentWell = "";

    [ObservableProperty]
    private string _mistakes = "";

    [ObservableProperty]
    private string _focusNext = "";

    [ObservableProperty]
    private string _reviewNotes = "";

    [ObservableProperty]
    private string _improvementNote = "";

    [ObservableProperty]
    private string _attribution = "";

    [ObservableProperty]
    private string _mentalHandled = "";

    [ObservableProperty]
    private string _spottedProblems = "";

    [ObservableProperty]
    private string _outsideControl = "";

    [ObservableProperty]
    private string _withinControl = "";

    [ObservableProperty]
    private string _personalContribution = "";

    [ObservableProperty]
    private bool _showMentalReflection;

    [ObservableProperty]
    private bool _isLoading;

    // ── Concept Tags ────────────────────────────────────────────────

    public ObservableCollection<ConceptTagItem> AllTags { get; } = new();
    public ObservableCollection<long> SelectedTagIds { get; } = new();

    // ── Attribution Options ─────────────────────────────────────────

    public static IReadOnlyList<string> AttributionOptions { get; } = new[]
    {
        "My play",
        "Team effort",
        "Teammates",
        "External"
    };

    // ── Constructor ─────────────────────────────────────────────────

    public ReviewViewModel(
        IGameRepository gameRepo,
        IConceptTagRepository conceptTagRepo,
        IVodRepository vodRepo,
        ISessionLogRepository sessionLogRepo,
        INavigationService navigationService,
        ILogger<ReviewViewModel> logger)
    {
        _gameRepo = gameRepo;
        _conceptTagRepo = conceptTagRepo;
        _vodRepo = vodRepo;
        _sessionLogRepo = sessionLogRepo;
        _navigationService = navigationService;
        _logger = logger;
    }

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync(long gameId)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            GameId = gameId;

            // Load game
            var game = await _gameRepo.GetAsync(gameId);
            if (game == null)
            {
                _logger.LogWarning("Game {GameId} not found", gameId);
                return;
            }

            // Populate game data
            ChampionName = game.ChampionName;
            Win = game.Win;
            ResultText = game.Win ? "VICTORY" : "DEFEAT";
            ResultColorHex = game.Win ? "#22c55e" : "#ef4444";
            KdaText = $"{game.Kills} / {game.Deaths} / {game.Assists}";
            KdaRatioText = $"{game.KdaRatio:F2} KDA";
            GameModeText = game.GameMode;
            DurationText = game.GameDuration > 0
                ? $"{game.GameDuration / 60}:{game.GameDuration % 60:D2}"
                : "";
            HeaderText = $"Review -- {game.ChampionName} ({(game.Win ? "W" : "L")})";

            // Stats
            DamageText = FormatNumber(game.TotalDamageToChampions);
            CsText = game.CsTotal.ToString();
            CsPerMinText = $"{game.CsPerMin:F1}/m";
            VisionText = game.VisionScore.ToString();
            GoldText = FormatNumber(game.GoldEarned);
            KillParticipationText = $"{game.KillParticipation:F0}%";
            DamageTakenText = FormatNumber(game.TotalDamageTaken);
            WardsPlacedText = game.WardsPlaced.ToString();

            // VOD
            var vod = await _vodRepo.GetVodAsync(gameId);
            HasVod = vod != null;
            if (HasVod)
            {
                BookmarkCount = await _vodRepo.GetBookmarkCountAsync(gameId);
            }

            // Load existing review data from RawStats
            LoadExistingReview(game);

            // Mental rating from session log
            var sessionEntry = await _sessionLogRepo.GetEntryAsync(gameId);
            if (sessionEntry != null)
            {
                MentalRating = sessionEntry.MentalRating;
                ImprovementNote = sessionEntry.ImprovementNote;
                MentalHandled = sessionEntry.MentalHandled;
            }
            UpdateMentalColor();

            // Concept tags
            var allTags = await _conceptTagRepo.GetAllAsync();
            var existingTagIds = await _conceptTagRepo.GetIdsForGameAsync(gameId);

            DispatcherHelper.RunOnUIThread(() =>
            {
                AllTags.Clear();
                SelectedTagIds.Clear();

                foreach (var tag in allTags)
                {
                    var id = tag.TryGetValue("id", out var idVal) ? Convert.ToInt64(idVal ?? 0) : 0;
                    var name = tag.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
                    var color = tag.TryGetValue("color", out var c) ? c?.ToString() ?? "#3b82f6" : "#3b82f6";
                    var isSelected = existingTagIds.Contains(id);

                    AllTags.Add(new ConceptTagItem
                    {
                        Id = id,
                        Name = name,
                        ColorHex = color,
                        IsSelected = isSelected
                    });

                    if (isSelected)
                    {
                        SelectedTagIds.Add(id);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load review for game {GameId}", gameId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveReviewAsync()
    {
        try
        {
            // Save review fields
            var review = new GameReview
            {
                Notes = ReviewNotes,
                Mistakes = Mistakes,
                WentWell = WentWell,
                FocusNext = FocusNext,
                SpottedProblems = SpottedProblems,
                OutsideControl = OutsideControl,
                WithinControl = WithinControl,
                Attribution = Attribution,
                PersonalContribution = PersonalContribution,
            };

            await _gameRepo.UpdateReviewAsync(GameId, review);

            // Save the session-log side of the review.
            await _sessionLogRepo.LogGameAsync(
                GameId,
                ChampionName,
                Win,
                MentalRating,
                ImprovementNote);

            // Save mental handled if low mental
            if (!string.IsNullOrWhiteSpace(MentalHandled))
            {
                await _sessionLogRepo.UpdateMentalHandledAsync(GameId, MentalHandled);
            }

            // Save concept tags
            var selectedIds = AllTags
                .Where(t => t.IsSelected)
                .Select(t => t.Id)
                .ToList();
            await _conceptTagRepo.SetForGameAsync(GameId, selectedIds);

            _logger.LogInformation("Review saved for game {GameId}", GameId);

            // Navigate back
            _navigationService.GoBack();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save review for game {GameId}", GameId);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _navigationService.GoBack();
    }

    [RelayCommand]
    private void WatchVod()
    {
        _navigationService.NavigateTo("vodplayer", GameId);
    }

    [RelayCommand]
    private void ToggleTag(ConceptTagItem tag)
    {
        tag.IsSelected = !tag.IsSelected;
        if (tag.IsSelected)
        {
            if (!SelectedTagIds.Contains(tag.Id))
                SelectedTagIds.Add(tag.Id);
        }
        else
        {
            SelectedTagIds.Remove(tag.Id);
        }
    }

    // ── Property change handlers ────────────────────────────────────

    partial void OnMentalRatingChanged(int value)
    {
        UpdateMentalColor();
        ShowMentalReflection = value <= 3;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void UpdateMentalColor()
    {
        MentalRatingColorHex = MentalRating switch
        {
            >= 8 => "#22c55e",
            >= 5 => "#0099ff",
            >= 4 => "#c89b3c",
            _ => "#ef4444"
        };
    }

    private void LoadExistingReview(GameStats game)
    {
        Mistakes = game.Mistakes;
        WentWell = game.WentWell;
        FocusNext = game.FocusNext;
        ReviewNotes = game.ReviewNotes;
        SpottedProblems = game.SpottedProblems;
        OutsideControl = game.OutsideControl;
        WithinControl = game.WithinControl;
        Attribution = game.Attribution;
        PersonalContribution = game.PersonalContribution;
    }

    private static string FormatNumber(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString()
    };
}

// ── Display models ──────────────────────────────────────────────────

/// <summary>Concept tag item with selection state for the tag selector.</summary>
public class ConceptTagItem : ObservableObject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    private string _colorHex = "#3b82f6";
    public string ColorHex
    {
        get => _colorHex;
        set
        {
            _colorHex = value;
            _colorBrush = null; // reset cached brush
        }
    }

    private SolidColorBrush? _colorBrush;
    /// <summary>SolidColorBrush derived from ColorHex for XAML binding.</summary>
    public SolidColorBrush ColorBrush
    {
        get
        {
            if (_colorBrush == null)
            {
                try
                {
                    var hex = ColorHex.TrimStart('#');
                    if (hex.Length == 6)
                    {
                        var r = Convert.ToByte(hex[..2], 16);
                        var g = Convert.ToByte(hex[2..4], 16);
                        var b = Convert.ToByte(hex[4..6], 16);
                        _colorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
                    }
                    else
                    {
                        _colorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 59, 130, 246)); // fallback blue
                    }
                }
                catch
                {
                    _colorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 59, 130, 246)); // fallback blue
                }
            }
            return _colorBrush;
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

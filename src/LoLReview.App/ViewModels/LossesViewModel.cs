#nullable enable

using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the Losses page — browse and analyze recent losses.</summary>
public partial class LossesViewModel : ObservableObject
{
    private readonly IGameRepository _games;
    private readonly INavigationService _navigation;
    private bool _isLoadingData;
    private bool _suppressFilterChange;

    public LossesViewModel()
    {
        _games = App.GetService<IGameRepository>();
        _navigation = App.GetService<INavigationService>();
    }

    // ── Properties ───────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ObservableCollection<LossCardModel> _losses = [];

    [ObservableProperty]
    private ObservableCollection<string> _champions = ["All Champions"];

    [ObservableProperty]
    private string _selectedChampion = "All Champions";

    partial void OnSelectedChampionChanged(string value)
    {
        // Guard: don't re-trigger load while we're rebuilding the champions list
        if (_suppressFilterChange || _isLoadingData) return;
        // Guard: null/empty can happen when ComboBox selection resets during Clear
        if (string.IsNullOrEmpty(value)) return;
        _ = LoadLossesOnlyAsync(value);
    }

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_isLoadingData) return;
        _isLoadingData = true;
        IsLoading = true;

        try
        {
            // Load champions for filter (suppress re-trigger during list rebuild)
            var champList = await _games.GetUniqueChampionsAsync(lossesOnly: true);
            var currentFilter = SelectedChampion;

            _suppressFilterChange = true;
            DispatcherHelper.RunOnUIThread(() =>
            {
                try
                {
                    Champions.Clear();
                    Champions.Add("All Champions");
                    foreach (var c in champList)
                        Champions.Add(c);

                    // Restore previous selection if it still exists, otherwise default
                    if (!string.IsNullOrEmpty(currentFilter) && Champions.Contains(currentFilter))
                        SelectedChampion = currentFilter;
                    else
                        SelectedChampion = "All Champions";
                }
                finally
                {
                    _suppressFilterChange = false;
                }
            });

            // Load losses
            var filter = SelectedChampion == "All Champions" ? null : SelectedChampion;
            var losses = await _games.GetLossesAsync(filter);

            DispatcherHelper.RunOnUIThread(() =>
            {
                Losses.Clear();
                foreach (var loss in losses)
                {
                    Losses.Add(LossCardModel.FromGameStats(loss));
                }
            });
        }
        catch (Exception)
        {
            // Best-effort load
        }
        finally
        {
            _isLoadingData = false;
            IsLoading = false;
        }
    }

    /// <summary>Reload only the losses list based on the selected champion filter.</summary>
    private async Task LoadLossesOnlyAsync(string champion)
    {
        if (_isLoadingData) return;
        _isLoadingData = true;
        IsLoading = true;

        try
        {
            var filter = string.IsNullOrEmpty(champion) || champion == "All Champions" ? null : champion;
            var losses = await _games.GetLossesAsync(filter);

            DispatcherHelper.RunOnUIThread(() =>
            {
                Losses.Clear();
                foreach (var loss in losses)
                {
                    Losses.Add(LossCardModel.FromGameStats(loss));
                }
            });
        }
        catch (Exception)
        {
            // Best-effort load
        }
        finally
        {
            _isLoadingData = false;
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void NavigateToReview(long gameId)
    {
        // Navigate to session page with the game id as parameter
        _navigation.NavigateTo("session", gameId);
    }
}

/// <summary>Display model for a single loss card.</summary>
public sealed class LossCardModel
{
    public long GameId { get; set; }
    public string ChampionName { get; set; } = "";
    public string DatePlayed { get; set; } = "";
    public string Duration { get; set; } = "";
    public string GameMode { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public double KdaRatio { get; set; }
    public string KdaDisplay => $"{Kills}/{Deaths}/{Assists}";
    public string KdaRatioDisplay => $"{KdaRatio:F2} KDA";
    public string SubtitleDisplay => $"{DatePlayed}  \u2022  {Duration}  \u2022  {GameMode}";
    public List<string> Tags { get; set; } = [];
    public bool HasTags => Tags.Count > 0;
    public string Mistakes { get; set; } = "";
    public bool HasMistakes => !string.IsNullOrWhiteSpace(Mistakes);
    public string FocusNext { get; set; } = "";
    public bool HasFocusNext => !string.IsNullOrWhiteSpace(FocusNext);
    public string SpottedProblems { get; set; } = "";
    public bool HasSpottedProblems => !string.IsNullOrWhiteSpace(SpottedProblems);
    public bool HasReview => HasMistakes || HasFocusNext;
    public string ReviewButtonText => HasReview ? "Edit Review" : "Review";

    public static LossCardModel FromGameStats(GameStats g)
    {
        List<string> tags = [];
        try
        {
            if (!string.IsNullOrEmpty(g.Tags) && g.Tags != "[]")
                tags = JsonSerializer.Deserialize<List<string>>(g.Tags) ?? [];
        }
        catch { /* ignore parse errors */ }

        return new LossCardModel
        {
            GameId = g.GameId,
            ChampionName = g.ChampionName,
            DatePlayed = g.DatePlayed,
            Duration = g.DurationFormatted,
            GameMode = g.GameMode,
            Kills = g.Kills,
            Deaths = g.Deaths,
            Assists = g.Assists,
            KdaRatio = g.KdaRatio,
            Tags = tags,
            Mistakes = g.Mistakes,
            FocusNext = g.FocusNext,
            SpottedProblems = g.SpottedProblems,
        };
    }
}

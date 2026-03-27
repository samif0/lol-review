#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the Analytics page — player profiling, suggestions, and stats.</summary>
public partial class AnalyticsViewModel : ObservableObject
{
    private readonly IAnalysisService _analysis;
    private readonly IObjectivesRepository _objectives;

    public AnalyticsViewModel()
    {
        _analysis = App.GetService<IAnalysisService>();
        _objectives = App.GetService<IObjectivesRepository>();
    }

    // ── Properties ───────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private PlayerProfile? _profile;

    [ObservableProperty]
    private ObservableCollection<SuggestionCardModel> _suggestions = [];

    [ObservableProperty]
    private ObservableCollection<ChampionStatRow> _championStats = [];

    [ObservableProperty]
    private ObservableCollection<MatchupStatRow> _matchupStats = [];

    [ObservableProperty]
    private ObservableCollection<TagFrequencyRow> _tagFrequencies = [];

    [ObservableProperty]
    private ObservableCollection<MentalBracketRow> _mentalBrackets = [];

    // ── Overall stat display ─────────────────────────────────────────

    [ObservableProperty]
    private string _totalGames = "0";

    [ObservableProperty]
    private string _winrate = "0%";

    [ObservableProperty]
    private SolidColorBrush _winrateColor = HexBrush("#a0a0b8");

    [ObservableProperty]
    private string _avgKda = "0.00";

    [ObservableProperty]
    private string _avgCsMin = "0.0";

    [ObservableProperty]
    private string _avgVision = "0";

    [ObservableProperty]
    private string _avgDeaths = "0.0";

    [ObservableProperty]
    private string _wins = "0";

    [ObservableProperty]
    private string _losses = "0";

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            var profile = await _analysis.GenerateProfileAsync();
            var suggestions = _analysis.GenerateSuggestions(profile, limit: 3);

            DispatcherHelper.RunOnUIThread(() =>
            {
                Profile = profile;
                PopulateOverallStats(profile);
                PopulateSuggestions(suggestions);
                PopulateChampionStats(profile);
                PopulateMatchupStats(profile);
                PopulateTagFrequencies(profile);
                PopulateMentalBrackets(profile);
            });
        }
        catch
        {
            // Best-effort load
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateObjectiveFromSuggestion(SuggestionCardModel? suggestion)
    {
        if (suggestion is null) return;

        try
        {
            await _objectives.CreateAsync(
                title: suggestion.Title,
                skillArea: suggestion.SkillArea,
                type: suggestion.Type,
                completionCriteria: suggestion.CompletionCriteria,
                description: suggestion.Description);

            // Mark as created in the UI
            suggestion.IsCreated = true;
        }
        catch
        {
            // Best-effort
        }
    }

    // ── Population helpers ───────────────────────────────────────────

    private void PopulateOverallStats(PlayerProfile p)
    {
        var o = p.Overall;
        TotalGames = o.TotalGames.ToString();
        Winrate = $"{o.Winrate:F1}%";
        WinrateColor = HexBrush(o.Winrate >= 50 ? "#22c55e" : "#ef4444");
        AvgKda = $"{o.AvgKda:F2}";
        AvgCsMin = $"{o.AvgCsMin:F1}";
        AvgVision = $"{o.AvgVision:F0}";
        AvgDeaths = $"{o.AvgDeaths:F1}";
        Wins = o.Wins.ToString();
        Losses = o.Losses.ToString();
    }

    private void PopulateSuggestions(List<ObjectiveSuggestion> list)
    {
        Suggestions.Clear();
        foreach (var s in list)
        {
            Suggestions.Add(new SuggestionCardModel
            {
                Title = s.Title,
                SkillArea = s.SkillArea,
                Type = s.Type,
                CompletionCriteria = s.CompletionCriteria,
                Description = s.Description,
                Reason = s.Reason,
                Confidence = s.Confidence,
                ConfidencePercent = (int)(s.Confidence * 100),
            });
        }
    }

    private void PopulateChampionStats(PlayerProfile p)
    {
        ChampionStats.Clear();
        var top = p.Champions
            .OrderByDescending(c => c.Games)
            .Take(10);
        foreach (var c in top)
        {
            ChampionStats.Add(new ChampionStatRow
            {
                ChampionName = c.ChampionName,
                Games = c.Games,
                Winrate = c.Winrate,
                WinrateDisplay = $"{c.Winrate:F0}%",
                WinrateColor = HexBrush(c.Winrate >= 55 ? "#22c55e" : c.Winrate < 45 ? "#ef4444" : "#e8e8f0"),
                AvgKda = $"{c.AvgKda:F1}",
                AvgCsMin = $"{c.AvgCsMin:F1}",
            });
        }
    }

    private void PopulateMatchupStats(PlayerProfile p)
    {
        MatchupStats.Clear();
        var worst = p.Matchups
            .Where(m => m.Games >= 2)
            .OrderBy(m => m.Winrate)
            .Take(10);
        foreach (var m in worst)
        {
            MatchupStats.Add(new MatchupStatRow
            {
                YourChampion = m.ChampionName,
                EnemyChampion = m.EnemyLaner,
                Games = m.Games,
                WinrateDisplay = $"{m.Winrate:F0}%",
                WinrateColor = HexBrush(m.Winrate >= 55 ? "#22c55e" : m.Winrate < 45 ? "#ef4444" : "#e8e8f0"),
                AvgKda = $"{m.AvgKda:F1}",
            });
        }
    }

    private void PopulateTagFrequencies(PlayerProfile p)
    {
        TagFrequencies.Clear();
        foreach (var t in p.ConceptTags.Take(12))
        {
            TagFrequencies.Add(new TagFrequencyRow
            {
                Name = t.Name,
                Count = t.Count,
                Polarity = t.Polarity,
                BarColor = HexBrush(t.Polarity == "negative" ? "#ef4444" :
                           t.Polarity == "positive" ? "#22c55e" : "#3b82f6"),
                BarWidth = Math.Max(20, Math.Min(300, t.Count * 20)),
            });
        }
    }

    private void PopulateMentalBrackets(PlayerProfile p)
    {
        MentalBrackets.Clear();
        var m = p.Mental;

        if (m.LowWr > 0 || m.MidWr > 0 || m.HighWr > 0)
        {
            MentalBrackets.Add(new MentalBracketRow
            {
                Bracket = "Low (1-3)",
                WinrateDisplay = $"{m.LowWr:F1}%",
                WinrateColor = HexBrush(m.LowWr >= 50 ? "#22c55e" : "#ef4444"),
                BarWidth = Math.Max(20, (int)(m.LowWr * 3)),
            });
            MentalBrackets.Add(new MentalBracketRow
            {
                Bracket = "Mid (4-6)",
                WinrateDisplay = $"{m.MidWr:F1}%",
                WinrateColor = HexBrush(m.MidWr >= 50 ? "#22c55e" : "#ef4444"),
                BarWidth = Math.Max(20, (int)(m.MidWr * 3)),
            });
            MentalBrackets.Add(new MentalBracketRow
            {
                Bracket = "High (7-10)",
                WinrateDisplay = $"{m.HighWr:F1}%",
                WinrateColor = HexBrush(m.HighWr >= 50 ? "#22c55e" : "#ef4444"),
                BarWidth = Math.Max(20, (int)(m.HighWr * 3)),
            });
        }
    }

    /// <summary>Parse a hex color string like "#22c55e" into a SolidColorBrush.</summary>
    internal static SolidColorBrush HexBrush(string hex)
    {
        hex = hex.TrimStart('#');
        var r = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
        var g = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
        var b = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
        return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
    }
}

// ── Display model records ───────────────────────────────────────────────

public sealed partial class SuggestionCardModel : ObservableObject
{
    public string Title { get; set; } = "";
    public string SkillArea { get; set; } = "";
    public string Type { get; set; } = "primary";
    public string CompletionCriteria { get; set; } = "";
    public string Description { get; set; } = "";
    public string Reason { get; set; } = "";
    public double Confidence { get; set; }
    public int ConfidencePercent { get; set; }

    [ObservableProperty]
    private bool _isCreated;

    public string ButtonText => IsCreated ? "Created" : "+ Create Objective";

    /// <summary>Helper for x:Bind in DataTemplate -- boolean negation.</summary>
    public bool NotCreated => !IsCreated;

    /// <summary>Helper for x:Bind in DataTemplate -- compute bar width from percent.</summary>
    public double ConfBarWidth => Math.Max(10, ConfidencePercent * 2.5);
}

public sealed class ChampionStatRow
{
    public string ChampionName { get; set; } = "";
    public int Games { get; set; }
    public double Winrate { get; set; }
    public string WinrateDisplay { get; set; } = "";
    public SolidColorBrush WinrateColor { get; set; } = new(ColorHelper.FromArgb(255, 232, 232, 240));
    public string AvgKda { get; set; } = "";
    public string AvgCsMin { get; set; } = "";
}

public sealed class MatchupStatRow
{
    public string YourChampion { get; set; } = "";
    public string EnemyChampion { get; set; } = "";
    public int Games { get; set; }
    public string WinrateDisplay { get; set; } = "";
    public SolidColorBrush WinrateColor { get; set; } = new(ColorHelper.FromArgb(255, 232, 232, 240));
    public string AvgKda { get; set; } = "";
}

public sealed class TagFrequencyRow
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public string Polarity { get; set; } = "";
    public SolidColorBrush BarColor { get; set; } = new(ColorHelper.FromArgb(255, 59, 130, 246));
    public double BarWidth { get; set; } = 20;
}

public sealed class MentalBracketRow
{
    public string Bracket { get; set; } = "";
    public string WinrateDisplay { get; set; } = "";
    public SolidColorBrush WinrateColor { get; set; } = new(ColorHelper.FromArgb(255, 232, 232, 240));
    public double BarWidth { get; set; } = 20;
}

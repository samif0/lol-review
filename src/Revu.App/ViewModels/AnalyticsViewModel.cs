#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.App.Helpers;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.ViewModels;

/// <summary>ViewModel for the Analytics page — player profiling and stats.</summary>
public partial class AnalyticsViewModel : ObservableObject
{
    private readonly IAnalysisService _analysis;
    private readonly ITiltCheckRepository _tiltChecks;

    public AnalyticsViewModel()
    {
        _analysis = App.GetService<IAnalysisService>();
        _tiltChecks = App.GetService<ITiltCheckRepository>();
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private PlayerProfile? _profile;

    [ObservableProperty]
    private ObservableCollection<ChampionStatRow> _championStats = [];

    [ObservableProperty]
    private ObservableCollection<MatchupStatRow> _matchupStats = [];

    [ObservableProperty]
    private ObservableCollection<TagFrequencyRow> _tagFrequencies = [];

    [ObservableProperty]
    private ObservableCollection<MentalBracketRow> _mentalBrackets = [];

    [ObservableProperty]
    private bool _hasTiltStats;

    [ObservableProperty]
    private string _tiltCheckCount = "0";

    [ObservableProperty]
    private string _avgTiltBefore = "0.0";

    [ObservableProperty]
    private string _avgTiltAfter = "0.0";

    [ObservableProperty]
    private string _avgTiltReduction = "0.0";

    [ObservableProperty]
    private string _topTiltEmotion = "";

    [ObservableProperty]
    private string _totalGames = "0";

    [ObservableProperty]
    private string _winrate = "0%";

    [ObservableProperty]
    private SolidColorBrush _winrateColor = AppSemanticPalette.Brush(AppSemanticPalette.SecondaryTextHex);

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

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            var profile = await _analysis.GenerateProfileAsync();
            var tiltStats = await _tiltChecks.GetStatsAsync();

            DispatcherHelper.RunOnUIThread(() =>
            {
                Profile = profile;
                PopulateOverallStats(profile);
                PopulateChampionStats(profile);
                PopulateMatchupStats(profile);
                PopulateTagFrequencies(profile);
                PopulateMentalBrackets(profile);
                PopulateTiltStats(tiltStats);
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

    private void PopulateOverallStats(PlayerProfile p)
    {
        var o = p.Overall;
        TotalGames = o.TotalGames.ToString();
        Winrate = $"{o.Winrate:F1}%";
        WinrateColor = AppSemanticPalette.Brush(o.Winrate >= 50
            ? AppSemanticPalette.PositiveHex
            : AppSemanticPalette.NegativeHex);
        AvgKda = $"{o.AvgKda:F2}";
        AvgCsMin = $"{o.AvgCsMin:F1}";
        AvgVision = $"{o.AvgVision:F0}";
        AvgDeaths = $"{o.AvgDeaths:F1}";
        Wins = o.Wins.ToString();
        Losses = o.Losses.ToString();
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
                WinrateColor = AppSemanticPalette.Brush(AppSemanticPalette.WinRateHex(c.Winrate)),
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
                WinrateColor = AppSemanticPalette.Brush(AppSemanticPalette.WinRateHex(m.Winrate)),
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
                BarColor = AppSemanticPalette.TagAccentBrush(t.Polarity),
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
                WinrateColor = AppSemanticPalette.Brush(m.LowWr >= 50
                    ? AppSemanticPalette.PositiveHex
                    : AppSemanticPalette.NegativeHex),
                BarWidth = Math.Max(20, (int)(m.LowWr * 3)),
            });
            MentalBrackets.Add(new MentalBracketRow
            {
                Bracket = "Mid (4-6)",
                WinrateDisplay = $"{m.MidWr:F1}%",
                WinrateColor = AppSemanticPalette.Brush(m.MidWr >= 50
                    ? AppSemanticPalette.PositiveHex
                    : AppSemanticPalette.NegativeHex),
                BarWidth = Math.Max(20, (int)(m.MidWr * 3)),
            });
            MentalBrackets.Add(new MentalBracketRow
            {
                Bracket = "High (7-10)",
                WinrateDisplay = $"{m.HighWr:F1}%",
                WinrateColor = AppSemanticPalette.Brush(m.HighWr >= 50
                    ? AppSemanticPalette.PositiveHex
                    : AppSemanticPalette.NegativeHex),
                BarWidth = Math.Max(20, (int)(m.HighWr * 3)),
            });
        }
    }

    private void PopulateTiltStats(TiltCheckStats stats)
    {
        HasTiltStats = stats.Total > 0;
        TiltCheckCount = stats.Total.ToString();
        AvgTiltBefore = $"{stats.AvgBefore:F1}";
        AvgTiltAfter = $"{stats.AvgAfter:F1}";
        AvgTiltReduction = $"{stats.AvgReduction:F1}";
        TopTiltEmotion = stats.TopEmotions.FirstOrDefault()?.Emotion ?? "";
    }

    internal static SolidColorBrush HexBrush(string hex)
    {
        hex = hex.TrimStart('#');
        var r = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
        var g = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
        var b = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
        return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
    }
}

public sealed class ChampionStatRow
{
    public string ChampionName { get; set; } = "";
    public int Games { get; set; }
    public double Winrate { get; set; }
    public string WinrateDisplay { get; set; } = "";
    public SolidColorBrush WinrateColor { get; set; } = AppSemanticPalette.Brush(AppSemanticPalette.PrimaryTextHex);
    public string AvgKda { get; set; } = "";
    public string AvgCsMin { get; set; } = "";
}

public sealed class MatchupStatRow
{
    public string YourChampion { get; set; } = "";
    public string EnemyChampion { get; set; } = "";
    public int Games { get; set; }
    public string WinrateDisplay { get; set; } = "";
    public SolidColorBrush WinrateColor { get; set; } = AppSemanticPalette.Brush(AppSemanticPalette.PrimaryTextHex);
    public string AvgKda { get; set; } = "";
}

public sealed class TagFrequencyRow
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public string Polarity { get; set; } = "";
    public SolidColorBrush BarColor { get; set; } = AppSemanticPalette.Brush(AppSemanticPalette.AccentBlueHex);
    public double BarWidth { get; set; } = 20;
}

public sealed class MentalBracketRow
{
    public string Bracket { get; set; } = "";
    public string WinrateDisplay { get; set; } = "";
    public SolidColorBrush WinrateColor { get; set; } = AppSemanticPalette.Brush(AppSemanticPalette.PrimaryTextHex);
    public double BarWidth { get; set; } = 20;
}

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
    private readonly IGameRepository _games;

    /// <summary>Last filter that was applied + fed to the analysis service.</summary>
    private AnalyticsFilter _appliedFilter = AnalyticsFilter.None;

    public AnalyticsViewModel()
    {
        _analysis = App.GetService<IAnalysisService>();
        _tiltChecks = App.GetService<ITiltCheckRepository>();
        _games = App.GetService<IGameRepository>();
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

    // ── Filter state (draft — committed via ApplyFiltersCommand) ─────

    /// <summary>Whether the filter bar is expanded in the UI.</summary>
    [ObservableProperty]
    private bool _filtersExpanded;

    /// <summary>One-line summary shown when the filter bar is collapsed.</summary>
    [ObservableProperty]
    private string _filtersSummary = "No filters applied";

    /// <summary>Full list of champions the user has played (fills the chip selector).</summary>
    public ObservableCollection<FilterChampionChip> AvailableChampions { get; } = new();

    /// <summary>Role chips the user toggles on/off.</summary>
    public ObservableCollection<FilterRoleChip> RoleChips { get; } = new()
    {
        new FilterRoleChip("TOP",     "TOP"),
        new FilterRoleChip("JUNGLE",  "JUNGLE"),
        new FilterRoleChip("MIDDLE",  "MID"),
        new FilterRoleChip("BOTTOM",  "BOT"),
        new FilterRoleChip("UTILITY", "SUP"),
    };

    /// <summary>Mental-bucket chips (1-3 / 4-6 / 7-10).</summary>
    public ObservableCollection<FilterMentalChip> MentalChips { get; } = new()
    {
        new FilterMentalChip(MentalBucket.Low,  "TILTED",  "1–3"),
        new FilterMentalChip(MentalBucket.Mid,  "MID",     "4–6"),
        new FilterMentalChip(MentalBucket.High, "LOCKED",  "7–10"),
    };

    /// <summary>Day-of-week chips for schedule filtering.</summary>
    public ObservableCollection<FilterDayChip> DayChips { get; } = new()
    {
        new FilterDayChip(DayOfWeek.Monday,    "MON"),
        new FilterDayChip(DayOfWeek.Tuesday,   "TUE"),
        new FilterDayChip(DayOfWeek.Wednesday, "WED"),
        new FilterDayChip(DayOfWeek.Thursday,  "THU"),
        new FilterDayChip(DayOfWeek.Friday,    "FRI"),
        new FilterDayChip(DayOfWeek.Saturday,  "SAT"),
        new FilterDayChip(DayOfWeek.Sunday,    "SUN"),
    };

    /// <summary>Win / Loss / Both — 0=both, 1=win, 2=loss.</summary>
    [ObservableProperty]
    private int _draftWinLossIndex;

    /// <summary>Date range preset — index matches DateRangePreset enum order.</summary>
    [ObservableProperty]
    private int _draftDateRangeIndex;

    /// <summary>Objective practice — index matches ObjectivePracticeFilter enum order.</summary>
    [ObservableProperty]
    private int _draftObjectivePracticeIndex;

    /// <summary>0 = AND (all dimensions), 1 = OR (any dimension).</summary>
    [ObservableProperty]
    private int _draftMatchModeIndex;

    /// <summary>True while draft != applied — lights up Apply button.</summary>
    [ObservableProperty]
    private bool _hasUnappliedChanges;

    [RelayCommand]
    private void ToggleFilterBar() => FiltersExpanded = !FiltersExpanded;

    [RelayCommand]
    private void ToggleRoleChip(FilterRoleChip chip)
    {
        chip.IsSelected = !chip.IsSelected;
        RefreshUnappliedFlag();
    }

    [RelayCommand]
    private void ToggleMentalChip(FilterMentalChip chip)
    {
        chip.IsSelected = !chip.IsSelected;
        RefreshUnappliedFlag();
    }

    [RelayCommand]
    private void ToggleDayChip(FilterDayChip chip)
    {
        chip.IsSelected = !chip.IsSelected;
        RefreshUnappliedFlag();
    }

    [RelayCommand]
    private void ToggleChampionChip(FilterChampionChip chip)
    {
        chip.IsSelected = !chip.IsSelected;
        RefreshUnappliedFlag();
    }

    [RelayCommand]
    private async Task ApplyFiltersAsync()
    {
        _appliedFilter = BuildFilterFromDraft();
        HasUnappliedChanges = false;
        FiltersSummary = DescribeFilter(_appliedFilter);
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResetFiltersAsync()
    {
        foreach (var c in RoleChips) c.IsSelected = false;
        foreach (var c in MentalChips) c.IsSelected = false;
        foreach (var c in DayChips) c.IsSelected = false;
        foreach (var c in AvailableChampions) c.IsSelected = false;
        DraftWinLossIndex = 0;
        DraftDateRangeIndex = 0;
        DraftObjectivePracticeIndex = 0;
        DraftMatchModeIndex = 0;
        _appliedFilter = AnalyticsFilter.None;
        HasUnappliedChanges = false;
        FiltersSummary = "No filters applied";
        await LoadAsync().ConfigureAwait(true);
    }

    partial void OnDraftWinLossIndexChanged(int value) => RefreshUnappliedFlag();
    partial void OnDraftDateRangeIndexChanged(int value) => RefreshUnappliedFlag();
    partial void OnDraftObjectivePracticeIndexChanged(int value) => RefreshUnappliedFlag();
    partial void OnDraftMatchModeIndexChanged(int value) => RefreshUnappliedFlag();

    private void RefreshUnappliedFlag()
    {
        var draft = BuildFilterFromDraft();
        HasUnappliedChanges = !FiltersEqual(draft, _appliedFilter);
    }

    private AnalyticsFilter BuildFilterFromDraft()
    {
        return new AnalyticsFilter
        {
            Champions = AvailableChampions.Where(c => c.IsSelected).Select(c => c.Name).ToList(),
            Roles = RoleChips.Where(c => c.IsSelected).Select(c => c.Code).ToList(),
            Win = DraftWinLossIndex switch { 1 => true, 2 => false, _ => null },
            MentalBuckets = MentalChips.Where(c => c.IsSelected).Select(c => c.Bucket).ToList(),
            DateRange = (DateRangePreset)DraftDateRangeIndex,
            DaysOfWeek = DayChips.Where(c => c.IsSelected).Select(c => c.Day).ToList(),
            ObjectivePractice = (ObjectivePracticeFilter)DraftObjectivePracticeIndex,
            MatchMode = (FilterMatchMode)DraftMatchModeIndex,
        };
    }

    private static bool FiltersEqual(AnalyticsFilter a, AnalyticsFilter b)
    {
        return a.Champions.SequenceEqual(b.Champions)
            && a.Roles.SequenceEqual(b.Roles)
            && a.Win == b.Win
            && a.MentalBuckets.SequenceEqual(b.MentalBuckets)
            && a.DateRange == b.DateRange
            && a.DaysOfWeek.SequenceEqual(b.DaysOfWeek)
            && a.ObjectivePractice == b.ObjectivePractice
            && a.MatchMode == b.MatchMode;
    }

    private static string DescribeFilter(AnalyticsFilter f)
    {
        if (f.IsEmpty) return "No filters applied";
        var parts = new List<string>(6);
        if (f.Champions.Count > 0) parts.Add($"{f.Champions.Count} champ{(f.Champions.Count == 1 ? "" : "s")}");
        if (f.Roles.Count > 0) parts.Add($"{f.Roles.Count} role{(f.Roles.Count == 1 ? "" : "s")}");
        if (f.Win is bool w) parts.Add(w ? "wins only" : "losses only");
        if (f.MentalBuckets.Count > 0) parts.Add($"{f.MentalBuckets.Count} mental");
        if (f.DateRange != DateRangePreset.All) parts.Add(f.DateRange.ToString());
        if (f.DaysOfWeek.Count > 0) parts.Add($"{f.DaysOfWeek.Count} day{(f.DaysOfWeek.Count == 1 ? "" : "s")}");
        if (f.ObjectivePractice != ObjectivePracticeFilter.Any) parts.Add(f.ObjectivePractice.ToString());
        var joiner = f.MatchMode == FilterMatchMode.All ? " AND " : " OR ";
        return string.Join(joiner, parts);
    }

    private async Task LoadAvailableChampionsAsync()
    {
        if (AvailableChampions.Count > 0) return;  // already populated
        try
        {
            var champs = await _games.GetChampionStatsAsync().ConfigureAwait(true);
            foreach (var c in champs.OrderByDescending(x => x.GamesPlayed))
            {
                AvailableChampions.Add(new FilterChampionChip(c.ChampionName));
            }
        }
        catch { /* no-op: filter bar stays empty if this fails */ }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            // Populate the champion chip list on first load so the filter
            // bar has something to show. Subsequent loads skip this.
            await LoadAvailableChampionsAsync().ConfigureAwait(true);

            var profile = _appliedFilter.IsEmpty
                ? await _analysis.GenerateProfileAsync()
                : await _analysis.GenerateProfileAsync(_appliedFilter);
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

// ── Filter chip view-models ──────────────────────────────────────────
//
// Each chip carries a display label + a selection flag the XAML toggles
// via a command. Kept partial so the [ObservableProperty] for IsSelected
// gives us INotifyPropertyChanged out of the box.

public partial class FilterChampionChip : ObservableObject
{
    public FilterChampionChip(string name) { Name = name; }
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;
}

public partial class FilterRoleChip : ObservableObject
{
    public FilterRoleChip(string code, string label) { Code = code; Label = label; }
    /// <summary>Riot internal role code — matches games.position values.</summary>
    public string Code { get; }
    /// <summary>Short label shown in the chip (TOP / MID / BOT / …).</summary>
    public string Label { get; }
    [ObservableProperty] private bool _isSelected;
}

public partial class FilterMentalChip : ObservableObject
{
    public FilterMentalChip(MentalBucket bucket, string label, string range)
    {
        Bucket = bucket; Label = label; Range = range;
    }
    public MentalBucket Bucket { get; }
    public string Label { get; }
    public string Range { get; }
    [ObservableProperty] private bool _isSelected;
}

public partial class FilterDayChip : ObservableObject
{
    public FilterDayChip(DayOfWeek day, string label) { Day = day; Label = label; }
    public DayOfWeek Day { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isSelected;
}

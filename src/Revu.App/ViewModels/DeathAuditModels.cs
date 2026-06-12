#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Revu.App.Styling;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.ViewModels;

/// <summary>
/// v2.18 (schema v5): one death from the live kill feed, ready for a one-tap
/// cause classification. Six chips per death; selecting one persists
/// immediately, tapping the selected chip clears back to unclassified.
/// </summary>
public sealed class DeathAuditItem
{
    public long GameId { get; init; }
    public int GameTimeSeconds { get; init; }
    public string TimeText => $"{GameTimeSeconds / 60:D2}:{GameTimeSeconds % 60:D2}";

    public ObservableCollection<DeathChipOption> Chips { get; } = new();
}

/// <summary>One selectable cause chip on a death row.</summary>
public partial class DeathChipOption : ObservableObject
{
    public long GameId { get; init; }
    public int GameTimeSeconds { get; init; }
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Hint { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipForegroundBrush))]
    [NotifyPropertyChangedFor(nameof(ChipBorderBrush))]
    private bool _isSelected;

    public SolidColorBrush ChipForegroundBrush => AppSemanticPalette.Brush(
        IsSelected ? AppSemanticPalette.AccentGoldHex : AppSemanticPalette.SecondaryTextHex);

    public SolidColorBrush ChipBorderBrush => AppSemanticPalette.Brush(
        IsSelected ? AppSemanticPalette.AccentGoldHex : AppSemanticPalette.SubtleBorderHex);
}

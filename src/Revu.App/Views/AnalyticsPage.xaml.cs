#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.Core.Lcu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.Views;

/// <summary>Analytics page — charts, trends, and player profiling.</summary>
public sealed partial class AnalyticsPage : Page
{
    public AnalyticsViewModel ViewModel { get; }

    public AnalyticsPage()
    {
        ViewModel = App.GetService<AnalyticsViewModel>();
        InitializeComponent();

        // Set DataContext so chip DataTemplates can reach the VM commands
        // via {Binding DataContext.ToggleXChipCommand, ElementName=RootGrid}.
        // Inside a DataTemplate the x:DataType is the chip row, not the VM,
        // so we walk out to the Page's DataContext for page-level commands.
        DataContext = ViewModel;

        WeakReferenceMessenger.Default.Register<AnalyticsPage, GameDeletedMessage>(
            this, async (r, _) => await r.ViewModel.LoadCommand.ExecuteAsync(null));
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AnimatePageEnter(RootGrid);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>Helper for x:Bind — returns Visible when count > 0.</summary>
    public Visibility HasItems(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Chip background — accent-tinted when selected, panel surface otherwise.</summary>
    public Brush ChipBg(bool isSelected)
    {
        var key = isSelected ? "AccentBlueDimBrush" : "InputBackgroundBrush";
        return (Brush)Application.Current.Resources[key];
    }

    /// <summary>Chip border — bright accent when selected, subtle otherwise.</summary>
    public Brush ChipBorder(bool isSelected)
    {
        var key = isSelected ? "AccentBlueBrush" : "SubtleBorderBrush";
        return (Brush)Application.Current.Resources[key];
    }

    /// <summary>Caret glyph for the filter expand/collapse button.</summary>
    public string ToggleGlyph(bool expanded)
        => expanded ? "\uE70E"   /* ChevronUp */
                    : "\uE70D"; /* ChevronDown */

    // Chip click handlers — DataTemplate → Page Command bindings are
    // unreliable in WinUI 3 (ElementName walk across DataTemplate boundaries
    // sometimes returns null at runtime), so we wire Click directly and
    // dispatch to the VM command with the Button's DataContext as the arg.

    private void RoleChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is FilterRoleChip chip)
            ViewModel.ToggleRoleChipCommand.Execute(chip);
    }

    private void MentalChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is FilterMentalChip chip)
            ViewModel.ToggleMentalChipCommand.Execute(chip);
    }

    private void DayChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is FilterDayChip chip)
            ViewModel.ToggleDayChipCommand.Execute(chip);
    }

    private void ChampionChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is FilterChampionChip chip)
            ViewModel.ToggleChampionChipCommand.Execute(chip);
    }
}

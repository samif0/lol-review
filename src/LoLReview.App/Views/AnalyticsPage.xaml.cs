#nullable enable

using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Views;

/// <summary>Analytics page — charts, trends, and player profiling.</summary>
public sealed partial class AnalyticsPage : Page
{
    public AnalyticsViewModel ViewModel { get; }

    public AnalyticsPage()
    {
        ViewModel = App.GetService<AnalyticsViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private async void OnCreateObjectiveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SuggestionCardModel suggestion)
        {
            await ViewModel.CreateObjectiveFromSuggestionCommand.ExecuteAsync(suggestion);
        }
    }

    /// <summary>Helper for x:Bind — returns Visible when count > 0.</summary>
    public Visibility HasItems(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Helper for x:Bind — returns Visible when suggestions exist.</summary>
    public Visibility HasSuggestions(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Helper for x:Bind — compute confidence bar width from percent.</summary>
    public static double ConfidenceBarWidth(int percent)
        => Math.Max(10, percent * 2.5);

    /// <summary>Helper for x:Bind — boolean negation.</summary>
    public static bool Not(bool value) => !value;
}

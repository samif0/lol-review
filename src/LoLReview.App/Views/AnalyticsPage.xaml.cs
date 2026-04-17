#nullable enable

using LoLReview.App.Helpers;
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
        AnimationHelper.AnimatePageEnter(RootGrid);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>Helper for x:Bind — returns Visible when count > 0.</summary>
    public Visibility HasItems(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

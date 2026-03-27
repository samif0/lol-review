#nullable enable

using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Views;

/// <summary>Losses page — focused loss analysis and pattern detection.</summary>
public sealed partial class LossesPage : Page
{
    public LossesViewModel ViewModel { get; }

    public LossesPage()
    {
        ViewModel = App.GetService<LossesViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.NavigateToReviewCommand.Execute(gameId);
        }
    }

    /// <summary>Helper for x:Bind — returns Visible when list is empty and not loading.</summary>
    public Visibility IsEmpty(int count, bool isLoading)
        => count == 0 && !isLoading ? Visibility.Visible : Visibility.Collapsed;
}

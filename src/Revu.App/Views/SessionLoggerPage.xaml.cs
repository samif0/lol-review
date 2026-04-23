#nullable enable

using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Views;

/// <summary>Session logger page — live game tracking and post-game review.</summary>
public sealed partial class SessionLoggerPage : Page
{
    public SessionLoggerViewModel ViewModel { get; }

    public SessionLoggerPage()
    {
        ViewModel = App.GetService<SessionLoggerViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AnimatePageEnter(RootGrid);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.NavigateToReviewCommand.Execute(gameId);
        }
    }

    private void OnManualEntryClick(object sender, RoutedEventArgs e)
    {
        var nav = App.GetService<INavigationService>();
        nav.NavigateTo("manualentry");
    }
}

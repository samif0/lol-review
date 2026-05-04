#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.Core.Lcu;
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

        // Reload whenever a game is deleted or reviewed from any page.
        // Without this, mutating a game on the Dashboard or History tab
        // would leave a stale row here until the user navigated away and back.
        WeakReferenceMessenger.Default.Register<SessionLoggerPage, GameDeletedMessage>(
            this, async (r, _) => await r.ViewModel.LoadCommand.ExecuteAsync(null));
        WeakReferenceMessenger.Default.Register<SessionLoggerPage, GameReviewedMessage>(
            this, async (r, _) => await r.ViewModel.LoadCommand.ExecuteAsync(null));

        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
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

    private void DeleteGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.DeleteGameCommand.Execute(gameId);
        }
    }

    private void SkipReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.SkipReviewCommand.Execute(gameId);
        }
    }

    private void ClearRuleBreakButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.ClearRuleBreakCommand.Execute(gameId);
        }
    }

    private void OnManualEntryClick(object sender, RoutedEventArgs e)
    {
        var nav = App.GetService<INavigationService>();
        nav.NavigateTo("manualentry");
    }
}

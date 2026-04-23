#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.Core.Lcu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Views;

/// <summary>History page — browse past games with filtering, stats overview, and champion breakdown.</summary>
public sealed partial class HistoryPage : Page
{
    public HistoryViewModel ViewModel { get; }

    /// <summary>Whether there are no champion stats to display.</summary>
    public bool HasNoChampionStats => ViewModel.ChampionStats.Count == 0;

    public HistoryPage()
    {
        ViewModel = App.GetService<HistoryViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        Loaded += OnLoaded;

        // Update computed properties when collections change
        ViewModel.ChampionStats.CollectionChanged += (_, _) => Bindings.Update();

        // Reload after a delete triggered anywhere else so stats + the list
        // reflect the new reality (champion-breakdown totals, win rate, etc.)
        WeakReferenceMessenger.Default.Register<HistoryPage, GameDeletedMessage>(
            this, async (r, _) => await r.ViewModel.LoadCommand.ExecuteAsync(null));
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AnimatePageEnter(RootGrid);

        if (ViewModel.LoadCommand.CanExecute(null))
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    private async void OnChampionFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // Avoid re-triggering during initial load
        if (!IsLoaded) return;

        if (ViewModel.FilterChangedCommand.CanExecute(null))
        {
            await ViewModel.FilterChangedCommand.ExecuteAsync(null);
        }
    }

    private async void OnWinLossFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // Avoid re-triggering during initial load
        if (!IsLoaded) return;

        if (sender is ComboBox combo)
        {
            ViewModel.SelectedWinLossFilter = combo.SelectedIndex;
        }

        if (ViewModel.FilterChangedCommand.CanExecute(null))
        {
            await ViewModel.FilterChangedCommand.ExecuteAsync(null);
        }
    }

    private void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.NavigateToReviewCommand.Execute(gameId);
        }
    }

    private void WatchVodButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.NavigateToVodPlayerCommand.Execute(gameId);
        }
    }

    private void DeleteGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.DeleteGameCommand.Execute(gameId);
        }
    }
}

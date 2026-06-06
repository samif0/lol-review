#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.Core.Lcu;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.Views;

public sealed partial class GamesPage : Page
{
    public GamesViewModel ViewModel { get; }

    public GamesPage()
    {
        ViewModel = App.GetService<GamesViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        Loaded += OnLoaded;
        WeakReferenceMessenger.Default.Register<GamesPage, GameDeletedMessage>(
            this, (r, _) => SafeHandler.Run(
                () => r.ViewModel.LoadCommand.ExecuteAsync(null), "GamesPage.GameDeleted reload"));
        WeakReferenceMessenger.Default.Register<GamesPage, GameReviewedMessage>(
            this, (r, _) => SafeHandler.Run(
                () => r.ViewModel.LoadCommand.ExecuteAsync(null), "GamesPage.GameReviewed reload"));
        WeakReferenceMessenger.Default.Register<GamesPage, GameMatchupsBackfilledMessage>(
            this, (r, _) =>
            {
                var ignored = DispatcherHelper.RunOnUIThreadAsync(
                    () => r.ViewModel.LoadCommand.ExecuteAsync(null));
            });
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AnimatePageEnter(RootGrid);
        if (ViewModel.LoadCommand.CanExecute(null))
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        UpdateSegmentButtons();
    }

    private async void SegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string raw } || !int.TryParse(raw, out var index)) return;

        if (ViewModel.SwitchViewCommand.CanExecute(index))
        {
            await ViewModel.SwitchViewCommand.ExecuteAsync(index);
            UpdateSegmentButtons();
        }
    }

    private void UpdateSegmentButtons()
    {
        var buttons = new[] { QueueSegment, TodaySegment, HistorySegment, VodSegment };
        for (var i = 0; i < buttons.Length; i++)
        {
            var active = i == ViewModel.SelectedViewIndex;
            buttons[i].Background = active
                ? (Brush)Application.Current.Resources["AccentPurpleBrush"]
                : new SolidColorBrush(Colors.Transparent);
            buttons[i].BorderBrush = active
                ? (Brush)Application.Current.Resources["AccentPurpleBrush"]
                : (Brush)Application.Current.Resources["SubtleBorderBrush"];
            buttons[i].Foreground = active
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 7, 6, 11))
                : (Brush)Application.Current.Resources["SecondaryTextBrush"];
        }
    }

    private void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long gameId })
        {
            ViewModel.PrimaryActionCommand.Execute(gameId);
        }
    }

    private void SkipReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long gameId })
        {
            ViewModel.SkipReviewCommand.Execute(gameId);
        }
    }

    private void DeleteGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long gameId })
        {
            ViewModel.DeleteGameCommand.Execute(gameId);
        }
    }

    // v2.17.8: row-body click → always the Review page. The inline action
    // buttons keep their own routes; ActionsStack_Tapped marks the tap handled
    // so a button click doesn't ALSO trigger this row-level navigate.
    private void GameRow_RowActivated(object sender, RoutedEventArgs e)
    {
        if (sender is Controls.GameRowCard { Tag: long gameId })
        {
            ViewModel.OpenReviewCommand.Execute(gameId);
        }
    }

    private void ActionsStack_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }
}

#nullable enable

using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Dialogs;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.App.Contracts;
using Revu.Core.Lcu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

/// <summary>Dashboard page — session overview, stats summary, and quick actions.</summary>
public sealed partial class DashboardPage : Page, INotifyPropertyChanged
{
    public DashboardViewModel ViewModel { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether there are active objectives to display.</summary>
    public bool HasObjectives => ViewModel.ActiveObjectives.Count > 0;

    /// <summary>Whether there are no today's games (for empty state).</summary>
    public bool HasNoTodaysGames => ViewModel.TodaysGames.Count == 0;

    public DashboardPage()
    {
        ViewModel = App.GetService<DashboardViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        // Update computed properties when collections change
        ViewModel.ActiveObjectives.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasObjectives));
            Bindings.Update();
        };
        ViewModel.TodaysGames.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoTodaysGames));
            Bindings.Update();
        };

        Loaded += (_, _) => AnimationHelper.AnimatePageEnter(RootGrid);

        // Reload stats + game lists whenever a game is deleted or reviewed
        // anywhere in the app so win-rate / adherence / unreviewed counts
        // stay accurate.
        WeakReferenceMessenger.Default.Register<DashboardPage, GameDeletedMessage>(
            this, (r, _) => SafeHandler.Run(
                () => r.ViewModel.LoadCommand.ExecuteAsync(null), "DashboardPage.GameDeleted reload"));
        WeakReferenceMessenger.Default.Register<DashboardPage, GameReviewedMessage>(
            this, (r, _) => SafeHandler.Run(
                () => r.ViewModel.LoadCommand.ExecuteAsync(null), "DashboardPage.GameReviewed reload"));
        WeakReferenceMessenger.Default.Register<DashboardPage, GameMatchupsBackfilledMessage>(
            this, (r, message) =>
            {
                var ignored = DispatcherHelper.RunOnUIThreadAsync(
                    () => r.ViewModel.LoadCommand.ExecuteAsync(null));
            });
        // A pattern marked reviewed should drop from the nag + bump the stat.
        WeakReferenceMessenger.Default.Register<DashboardPage, PatternReviewedMessage>(
            this, (r, _) => SafeHandler.Run(
                () => r.ViewModel.LoadCommand.ExecuteAsync(null), "DashboardPage.PatternReviewed reload"));
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (ViewModel.LoadCommand.CanExecute(null))
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }

        // Tilt Check (and anything else) can land the user straight into the
        // intent ritual: NavigateTo("dashboard", "startblock").
        if (e.Parameter is string param
            && string.Equals(param, "startblock", StringComparison.OrdinalIgnoreCase))
        {
            StartBlockButton_Click(this, new RoutedEventArgs());
        }
    }

    private void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.NavigateToReviewCommand.Execute(gameId);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

    private void ObjectivePatternOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ObjectivePatternItem pattern })
        {
            ViewModel.OpenObjectivePatternCommand.Execute(pattern);
        }
    }

    private void ReviewInboxButton_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().NavigateTo("session");
    }

    private void TiltResetButton_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().NavigateTo("tiltcheck");
    }

    private async void StartBlockButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new StartBlockDialog
            {
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();

            // The dialog may have locked in a session goal — reload so the
            // intent hero collapses to the locked chip immediately.
            if (ViewModel.LoadCommand.CanExecute(null))
            {
                await ViewModel.LoadCommand.ExecuteAsync(null);
            }
        }
        catch
        {
            // Dialog failure (e.g. one already open) is non-fatal; the
            // dashboard simply doesn't refresh.
        }
    }
}

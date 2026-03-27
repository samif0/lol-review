#nullable enable

using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LoLReview.App.Views;

/// <summary>Dashboard page — session overview, stats summary, and quick actions.</summary>
public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; }

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
        ViewModel.ActiveObjectives.CollectionChanged += (_, _) => Bindings.Update();
        ViewModel.TodaysGames.CollectionChanged += (_, _) => Bindings.Update();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (ViewModel.LoadCommand.CanExecute(null))
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    private void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.NavigateToReviewCommand.Execute(gameId);
        }
    }
}

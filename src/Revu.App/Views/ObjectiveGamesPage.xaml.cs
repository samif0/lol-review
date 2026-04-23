#nullable enable

using Revu.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

/// <summary>Shows all games linked to a specific objective.</summary>
public sealed partial class ObjectiveGamesPage : Page
{
    public ObjectiveGamesViewModel ViewModel { get; }

    public ObjectiveGamesPage()
    {
        ViewModel = App.GetService<ObjectiveGamesViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is long objectiveId)
        {
            ViewModel.LoadCommand.Execute(objectiveId);
        }
    }

    private void ReviewGame_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.OpenReviewCommand.Execute(gameId);
        }
    }
}

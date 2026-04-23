#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Revu.App.ViewModels;
using Revu.Core.Lcu;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

/// <summary>Shows all games linked to a specific objective.</summary>
public sealed partial class ObjectiveGamesPage : Page
{
    public ObjectiveGamesViewModel ViewModel { get; }

    private long? _currentObjectiveId;

    public ObjectiveGamesPage()
    {
        ViewModel = App.GetService<ObjectiveGamesViewModel>();
        InitializeComponent();

        // Reload the current objective's game list if any game was deleted
        // (it may have been one of the ones linked to this objective).
        WeakReferenceMessenger.Default.Register<ObjectiveGamesPage, GameDeletedMessage>(
            this, (r, _) =>
            {
                if (r._currentObjectiveId is long id)
                {
                    r.ViewModel.LoadCommand.Execute(id);
                }
            });
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is long objectiveId)
        {
            _currentObjectiveId = objectiveId;
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

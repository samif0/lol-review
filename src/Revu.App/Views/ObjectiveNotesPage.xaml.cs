#nullable enable

using System.Linq;
using Revu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

/// <summary>Shows all review notes, execution notes, and clips linked to a single objective.</summary>
public sealed partial class ObjectiveNotesPage : Page
{
    public ObjectiveNotesViewModel ViewModel { get; }

    public ObjectiveNotesPage()
    {
        ViewModel = App.GetService<ObjectiveNotesViewModel>();
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

    private void OpenReview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long gameId)
        {
            ViewModel.OpenReviewCommand.Execute(gameId);
        }
    }

    private void PlayBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long bookmarkId)
        {
            var row = ViewModel.Bookmarks.FirstOrDefault(b => b.BookmarkId == bookmarkId);
            if (row is not null)
            {
                ViewModel.PlayBookmarkCommand.Execute(row);
            }
        }
    }
}

#nullable enable

using LoLReview.App.Contracts;
using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LoLReview.App.Views;

/// <summary>Post-game review page shown after a match ends.</summary>
public sealed partial class PostGamePage : Page
{
    public ReviewViewModel ReviewVM { get; }

    private readonly INavigationService _navigationService;

    public PostGamePage()
    {
        ReviewVM = App.GetService<ReviewViewModel>();
        _navigationService = App.GetService<INavigationService>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is long gameId)
        {
            ReviewVM.LoadCommand.Execute(gameId);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ReviewVM.SaveReviewCommand.Execute(null);
        _navigationService.NavigateTo("session");
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        _navigationService.NavigateTo("session");
    }
}

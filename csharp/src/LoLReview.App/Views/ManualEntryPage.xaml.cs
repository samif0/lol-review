#nullable enable

using LoLReview.App.Contracts;
using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LoLReview.App.Views;

/// <summary>Full-page manual game entry — mirrors the post-game review layout.</summary>
public sealed partial class ManualEntryPage : Page
{
    public ManualEntryDialogViewModel ViewModel { get; }

    private readonly INavigationService _navigationService;

    public ManualEntryPage()
    {
        ViewModel = App.GetService<ManualEntryDialogViewModel>();
        _navigationService = App.GetService<INavigationService>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadObjectivesCommand.Execute(null);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var saved = await ViewModel.SaveAsync();
        if (saved)
        {
            _navigationService.NavigateTo("session");
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _navigationService.NavigateTo("session");
    }
}

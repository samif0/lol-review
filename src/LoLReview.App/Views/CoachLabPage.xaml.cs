#nullable enable

using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Views;

public sealed partial class CoachLabPage : Page
{
    public CoachLabViewModel ViewModel { get; }

    public CoachLabPage()
    {
        ViewModel = App.GetService<CoachLabViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }
}

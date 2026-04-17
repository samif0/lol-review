#nullable enable

using LoLReview.App.Helpers;
using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LoLReview.App.Views;

/// <summary>Pre-game focus page shown during champion select.</summary>
public sealed partial class PreGamePage : Page
{
    public PreGameDialogViewModel ViewModel { get; }

    public PreGamePage()
    {
        ViewModel = App.GetService<PreGameDialogViewModel>();
        InitializeComponent();
        Loaded += (_, _) => AnimationHelper.AnimatePageEnter(RootScroll);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var champInfo = e.Parameter as PreGameChampInfo;
        ViewModel.LoadCommand.Execute(champInfo);
    }
}

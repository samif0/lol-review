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
        ViewModel.Attach();
        ViewModel.LoadCommand.Execute(champInfo);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    /// <summary>x:Bind helper — show a placeholder until the enemy laner locks.</summary>
    public string EnemyOrPlaceholder(string? enemy)
        => string.IsNullOrEmpty(enemy) ? "..." : enemy;
}

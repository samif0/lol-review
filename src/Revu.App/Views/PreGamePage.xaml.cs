#nullable enable

using Revu.App.Helpers;
using Revu.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

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

    // x:Bind helpers for the intent card's cleared state (the inverse case
    // has no converter in this page's resources).
    public Microsoft.UI.Xaml.Visibility VisibleWhen(bool value)
        => value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility CollapsedWhen(bool value)
        => value ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}

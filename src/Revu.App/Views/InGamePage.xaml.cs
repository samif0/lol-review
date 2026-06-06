#nullable enable

using Revu.App.Helpers;
using Revu.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

/// <summary>
/// v2.18 (F6): in-game reference page — enemy cooldowns, matchup, and the
/// pre-game notes/answer boxes, so alt-tabbing back mid-game surfaces all of it.
/// Reuses <see cref="PreGameDialogViewModel"/> (same data + bindings as
/// <see cref="PreGamePage"/>); the ViewModel's ChampSelectUpdated subscription
/// keeps it live, and prompt answers persist by session_key so they carry over
/// from champ select. Auto-navigated on GameInProgressMessage; also reachable
/// from the sidebar "In Game" item.
/// </summary>
public sealed partial class InGamePage : Page
{
    public PreGameDialogViewModel ViewModel { get; }

    public InGamePage()
    {
        ViewModel = App.GetService<PreGameDialogViewModel>();
        InitializeComponent();
        Loaded += (_, _) => AnimationHelper.AnimatePageEnter(RootScroll);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Reuse the champ info if the navigator passed it (auto-nav from
        // GameInProgress); otherwise load from whatever the LCU last reported.
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

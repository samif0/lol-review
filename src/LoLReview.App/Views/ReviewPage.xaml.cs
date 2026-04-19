#nullable enable

using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.Services;
using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LoLReview.App.Views;

/// <summary>Inline game review page -- navigated to from game list or dashboard.</summary>
public sealed partial class ReviewPage : Page
{
    public ReviewViewModel ViewModel { get; }

    public ReviewPage()
    {
        ViewModel = App.GetService<ReviewViewModel>();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReviewViewModel.Attribution))
            {
                Bindings.Update();
            }
        };
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AnimationHelper.AnimatePageEnter(RootGrid);
            AskCoachBanner.Visibility = CoachFeatureFlag.IsEnabled()
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is long gameId)
        {
            ViewModel.LoadCommand.Execute(gameId);
        }
    }

    private void OnAskCoachAboutGameClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.GameId <= 0) return;

        var label = string.IsNullOrWhiteSpace(ViewModel.ChampionName)
            ? $"Game #{ViewModel.GameId}"
            : $"{ViewModel.ChampionName} (#{ViewModel.GameId})";

        var args = new CoachScopeArgs(
            Scope: new CoachScope(GameId: ViewModel.GameId),
            Label: label,
            SeedQuestion: null);

        App.GetService<INavigationService>().NavigateTo("coach", args);
    }

    // ── Attribution radio button helpers ─────────────────────────────

    public bool IsMyPlaySelected => ViewModel.Attribution == "My play";
    public bool IsTeamEffortSelected => ViewModel.Attribution == "Team effort";
    public bool IsTeammatesSelected => ViewModel.Attribution == "Teammates";
    public bool IsExternalSelected => ViewModel.Attribution == "External";

    private void OnAttributionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            ViewModel.Attribution = tag;
        }
    }
}

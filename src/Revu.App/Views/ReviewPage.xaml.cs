#nullable enable

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Services;
using Revu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

/// <summary>Inline game review page -- navigated to from game list or dashboard.</summary>
public sealed partial class ReviewPage : Page, INotifyPropertyChanged
{
    public ReviewViewModel ViewModel { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReviewPage()
    {
        ViewModel = App.GetService<ReviewViewModel>();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReviewViewModel.Attribution))
            {
                OnPropertyChanged(nameof(IsMyPlaySelected));
                OnPropertyChanged(nameof(IsTeamEffortSelected));
                OnPropertyChanged(nameof(IsTeammatesSelected));
                OnPropertyChanged(nameof(IsExternalSelected));
                Bindings.Update();
            }
        };
        InitializeComponent();
        EvidenceList.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(EvidenceList_PointerWheelChanged),
            handledEventsToo: true);
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

    public bool Not(bool value) => !value;

    public Visibility HasText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    private void OnAttributionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            ViewModel.Attribution = tag;
        }
    }

    private async void OnEvidenceTagChosen(object sender, Controls.ObjectivePicker.TagChosenEventArgs e)
    {
        if (e.Payload is not EvidenceInboxItem evidence) return;
        if (e.Option.ObjectiveId == evidence.ObjectiveId) return;

        await ViewModel.SetEvidenceObjectiveCommand.ExecuteAsync(
            new EvidenceObjectiveUpdateRequest(evidence, e.Option.ObjectiveId));
    }

    private async void OnEvidenceStatusClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not EvidenceInboxItem evidence
            || button.Tag is not string status)
        {
            return;
        }

        await ViewModel.SetEvidenceStatusCommand.ExecuteAsync(
            new EvidenceStatusUpdateRequest(evidence, status));
    }

    private async void OnEvidencePolarityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not EvidenceInboxItem evidence
            || button.Tag is not string polarity)
        {
            return;
        }

        await ViewModel.SetEvidencePolarityCommand.ExecuteAsync(
            new EvidencePolarityUpdateRequest(evidence, polarity));
    }

    private async void OnOpenEvidenceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EvidenceInboxItem evidence })
        {
            await ViewModel.OpenEvidenceInVodCommand.ExecuteAsync(evidence);
        }
    }

    private void EvidenceList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var scroller = FindDescendant<ScrollViewer>(EvidenceList);
        if (scroller is null)
        {
            return;
        }

        ScrollInnerViewer(scroller, e);
    }

    private static void ScrollInnerViewer(ScrollViewer scroller, PointerRoutedEventArgs e)
    {
        if (scroller.ScrollableHeight <= 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(scroller).Properties.MouseWheelDelta;
        var canScroll = delta < 0
            ? scroller.VerticalOffset < scroller.ScrollableHeight
            : scroller.VerticalOffset > 0;

        if (!canScroll)
        {
            return;
        }

        var nextOffset = Math.Clamp(scroller.VerticalOffset - delta, 0, scroller.ScrollableHeight);
        scroller.ChangeView(null, nextOffset, null, disableAnimation: true);
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

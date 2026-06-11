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
        Loaded += (_, _) =>
        {
            AnimationHelper.AnimatePageEnter(RootGrid);
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

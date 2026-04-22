#nullable enable

using LoLReview.App.Helpers;
using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Views;

/// <summary>Tilt check page — guided multi-step exercise to assess and reset mental state.</summary>
public sealed partial class TiltCheckPage : Page
{
    public TiltCheckViewModel ViewModel { get; }

    public TiltCheckPage()
    {
        ViewModel = App.GetService<TiltCheckViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += (_, _) => AnimationHelper.AnimatePageEnter(RootScroll);
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var info = e.Parameter as TiltCheckInfo;
        ViewModel.StartCommand.Execute(info);
    }

    /// <summary>Helper for x:Bind — returns Visible when currentStep matches target.</summary>
    public Visibility IsStepVisible(int currentStep, int targetStep)
        => currentStep == targetStep ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Helper for x:Bind — returns Visible when text is non-empty.</summary>
    public Visibility HasText(string? text)
        => !string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

    // ── Item-template click handlers ─────────────────────────────────────
    //
    // DataTemplates inside ItemsControl have their own namescope, so
    // {Binding ElementName=...} to reach the page's DataContext can fail
    // silently. Code-behind Tag-dispatch is more reliable.

    private void OnEmotionChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
            ViewModel.ToggleEmotionCommand.Execute(name);
    }

    private void OnReframeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text })
            ViewModel.SelectReframeCommand.Execute(text);
    }

    private void OnTriggerClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text })
            ViewModel.SelectTriggerCommand.Execute(text);
    }

    private void OnResponseClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text })
            ViewModel.SelectResponseCommand.Execute(text);
    }
}

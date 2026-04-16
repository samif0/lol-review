#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.ViewModels;
using LoLReview.Core.Lcu;
using LoLReview.Core.Models;
using Microsoft.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using System.Text.Json;

namespace LoLReview.App.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }

    private readonly INavigationService _navigationService;
    private readonly ILcuClient _lcuClient;
    private Button? _activeNavButton;
    private bool _startupInitialized;
    private CompositionRoundedRectangleGeometry? _contentViewportGeometry;
    private CompositionGeometricClip? _contentViewportClip;

    private static readonly SolidColorBrush ActiveBg = new(ColorHelper.FromArgb(255, 22, 20, 48));       // #161430 sidebar active bg
    private static readonly SolidColorBrush ActiveFg = new(ColorHelper.FromArgb(255, 240, 238, 248));     // #F0EEF8 text primary
    private static readonly SolidColorBrush ActiveBorder = new(ColorHelper.FromArgb(255, 167, 139, 250)); // #A78BFA sidebar active (violet)
    private static readonly SolidColorBrush InactiveBg = new(ColorHelper.FromArgb(24, 17, 15, 30));       // #110F1E sidebar hover (low alpha)
    private static readonly SolidColorBrush InactiveBorder = new(ColorHelper.FromArgb(255, 36, 32, 58));  // #24203A border
    private static readonly SolidColorBrush InactiveFg = new(ColorHelper.FromArgb(255, 122, 110, 150));   // #7A6E96 text secondary

    public ShellPage()
    {
        ViewModel = App.GetService<ShellViewModel>();
        _navigationService = App.GetService<INavigationService>();
        _lcuClient = App.GetService<ILcuClient>();

        InitializeComponent();

        // Initialize the navigation service with the frame (no NavigationView needed)
        _navigationService.Initialize(ContentFrame);

        RefreshCoachLabVisibility();

        // Select dashboard by default
        SetActiveNav(NavDashboard);
        _navigationService.NavigateTo("dashboard");

        WeakReferenceMessenger.Default.Register<LcuConnectionChangedMessage>(this, OnConnectionChanged);

        // Initialize DialogService with XamlRoot once the page is loaded
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_startupInitialized)
        {
            return;
        }

        _startupInitialized = true;
        AppDiagnostics.WriteVerbose("startup.log", "ShellPage.Loaded start");

        var dialogService = App.GetService<IDialogService>();
        dialogService.Initialize(XamlRoot);
        AppDiagnostics.WriteVerbose("startup.log", "ShellPage.Loaded dialog service initialized");

        await ViewModel.InitializeAsync();
        AppDiagnostics.WriteVerbose("startup.log", "ShellPage.Loaded view model initialized");

        await RefreshCoachLabVisibilityAsync();
        AppDiagnostics.WriteVerbose("startup.log", "ShellPage.Loaded coach lab visibility refreshed");

        UpdateContentViewportClip();
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            SetActiveNav(btn);
            _navigationService.NavigateTo(tag);
        }
    }

    private void SetActiveNav(Button btn)
    {
        if (_activeNavButton is not null)
        {
            _activeNavButton.Background = InactiveBg;
            _activeNavButton.Foreground = InactiveFg;
            _activeNavButton.BorderBrush = InactiveBorder;
        }

        btn.Background = ActiveBg;
        btn.Foreground = ActiveFg;
        btn.BorderBrush = ActiveBorder;
        _activeNavButton = btn;
    }

    /// <summary>
    /// Syncs sidebar selection when navigation happens programmatically (e.g. from ReviewPage -> VodPlayer).
    /// </summary>
    public void SyncSelection(string pageKey)
    {
        foreach (var child in NavPanel.Children)
        {
            if (child is Button btn && btn.Tag is string tag &&
                string.Equals(tag, pageKey, StringComparison.OrdinalIgnoreCase))
            {
                SetActiveNav(btn);
                return;
            }
        }

        if (string.Equals(NavSettings.Tag as string, pageKey, StringComparison.OrdinalIgnoreCase))
        {
            SetActiveNav(NavSettings);
        }
    }

    private void OnConnectionChanged(object recipient, LcuConnectionChangedMessage message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var brush = message.IsConnected
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 126, 201, 160))  // #7EC9A0 positive
                : new SolidColorBrush(ColorHelper.FromArgb(255, 211, 140, 144)); // #D38C90 negative

            ConnectionIndicator.Fill = brush;
            StatusDot.Background = brush;
            ConnectionStatusText.Text = "LCU";
        });

        _ = RefreshCoachLabVisibilityAsync();
    }

    private void RefreshCoachLabVisibility()
    {
        NavCoachLab.Visibility = CoachLabFeature.IsEnabled()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task RefreshCoachLabVisibilityAsync()
    {
        string? puuid = null;
        string? summonerName = null;

        try
        {
            var summoner = await _lcuClient.GetCurrentSummonerAsync().ConfigureAwait(false);
            if (summoner is JsonElement summonerEl)
            {
                if (summonerEl.TryGetProperty("puuid", out var puuidProp))
                {
                    puuid = puuidProp.GetString();
                }

                if (summonerEl.TryGetProperty("displayName", out var displayNameProp))
                {
                    summonerName = displayNameProp.GetString();
                }
                else if (summonerEl.TryGetProperty("gameName", out var gameNameProp))
                {
                    summonerName = gameNameProp.GetString();
                }
            }
        }
        catch
        {
            // Best effort. Local owner-file gating can still allow access without League running.
        }

        CoachLabFeature.UpdateRuntimeIdentity(puuid, summonerName);
        DispatcherQueue.TryEnqueue(RefreshCoachLabVisibility);
    }

    private void OnContentViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateContentViewportClip();
    }

    private void UpdateContentViewportClip()
    {
        if (ContentViewport.ActualWidth <= 0 || ContentViewport.ActualHeight <= 0)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(ContentViewport);
        var compositor = visual.Compositor;

        _contentViewportGeometry ??= compositor.CreateRoundedRectangleGeometry();
        _contentViewportClip ??= compositor.CreateGeometricClip(_contentViewportGeometry);

        _contentViewportGeometry.CornerRadius = new Vector2(2f, 2f);
        _contentViewportGeometry.Size = new Vector2((float)ContentViewport.ActualWidth, (float)ContentViewport.ActualHeight);
        visual.Clip = _contentViewportClip;
    }
}

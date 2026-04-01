#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.ViewModels;
using LoLReview.Core.Lcu;
using LoLReview.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace LoLReview.App.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }

    private readonly INavigationService _navigationService;
    private readonly ILcuClient _lcuClient;
    private Button? _activeNavButton;
    private bool _startupInitialized;

    private static readonly SolidColorBrush ActiveBg = new(ColorHelper.FromArgb(255, 0, 153, 255)); // #0099ff
    private static readonly SolidColorBrush ActiveFg = new(Colors.White);
    private static readonly SolidColorBrush InactiveBg = new(Colors.Transparent);
    private static readonly SolidColorBrush InactiveFg = new(ColorHelper.FromArgb(255, 192, 192, 216)); // #c0c0d8

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
        // Deactivate previous
        if (_activeNavButton is not null)
        {
            _activeNavButton.Background = InactiveBg;
            _activeNavButton.Foreground = InactiveFg;
        }

        // Activate new
        btn.Background = new SolidColorBrush(ColorHelper.FromArgb(40, 0, 153, 255)); // subtle blue
        btn.Foreground = ActiveFg;
        _activeNavButton = btn;
    }

    /// <summary>
    /// Syncs sidebar selection when navigation happens programmatically (e.g. from ReviewPage -> VodPlayer).
    /// </summary>
    public void SyncSelection(string pageKey)
    {
        // Find the button with the matching tag
        foreach (var child in NavPanel.Children)
        {
            if (child is Button btn && btn.Tag is string tag &&
                string.Equals(tag, pageKey, StringComparison.OrdinalIgnoreCase))
            {
                SetActiveNav(btn);
                return;
            }
        }

        // Check settings button
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
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 34, 197, 94))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68));

            ConnectionIndicator.Fill = brush;
            StatusDot.Fill = brush;
            ConnectionStatusText.Text = message.IsConnected ? "Connected" : "Waiting for League...";
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
}

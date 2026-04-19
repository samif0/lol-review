#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.ViewModels;
using LoLReview.App.Services;
using LoLReview.Core.Lcu;
using Microsoft.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace LoLReview.App.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }

    private readonly INavigationService _navigationService;
    private Button? _activeNavButton;
    private bool _startupInitialized;
    private CompositionRoundedRectangleGeometry? _contentViewportGeometry;
    private CompositionGeometricClip? _contentViewportClip;

    // Mockup: active = subtle 8% violet bg + violet glyph + thin right-edge accent (handled by ActiveBar)
    // No border box — keep it minimal.
    private static readonly SolidColorBrush ActiveBg = new(ColorHelper.FromArgb(20, 167, 139, 250));       // 8% violet
    private static readonly SolidColorBrush ActiveFg = new(ColorHelper.FromArgb(255, 167, 139, 250));      // #A78BFA violet
    private static readonly SolidColorBrush InactiveBg = new(ColorHelper.FromArgb(0, 0, 0, 0));            // transparent
    private static readonly SolidColorBrush InactiveFg = new(ColorHelper.FromArgb(255, 74, 62, 96));       // #4A3E60 muted text

    public ShellPage()
    {
        ViewModel = App.GetService<ShellViewModel>();
        _navigationService = App.GetService<INavigationService>();

        InitializeComponent();

        // Initialize the navigation service with the frame (no NavigationView needed)
        _navigationService.Initialize(ContentFrame);

        // Coach is alpha — hidden by default, revealed via env var or settings toggle.
        NavCoach.Visibility = CoachFeatureFlag.IsEnabled()
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        // Select dashboard by default
        SetActiveNav(NavDashboard);
        _navigationService.NavigateTo("dashboard");

        WeakReferenceMessenger.Default.Register<LcuConnectionChangedMessage>(this, OnConnectionChanged);

        // Initialize DialogService with XamlRoot once the page is loaded
        Loaded += OnLoaded;

        // Global keyboard handler for zoom (Ctrl+/Ctrl-)
        AddHandler(KeyDownEvent, new KeyEventHandler(OnShellKeyDown), handledEventsToo: true);
    }

    private void OnShellKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;

        // Throttle: Windows key-repeat fires KeyDown ~30/sec when held.
        // Each zoom change forces the whole content tree to re-rasterize at
        // the new scale, which can overwhelm weaker GPUs (a user reported
        // their entire PC crashing while holding Ctrl-=). One zoom step per
        // ~50 ms gives responsive feel but caps the repaint rate.
        var now = Environment.TickCount;
        if (now - _lastZoomTick < ZoomThrottleMs) return;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)187: // OemPlus (=/+)
                _lastZoomTick = now;
                SetZoom(_zoomLevel + ZoomStep);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Subtract:
            case (Windows.System.VirtualKey)189: // OemMinus (-/_)
                _lastZoomTick = now;
                SetZoom(_zoomLevel - ZoomStep);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Number0:
            case Windows.System.VirtualKey.NumberPad0:
                _lastZoomTick = now;
                SetZoom(1.0);
                e.Handled = true;
                break;
        }
    }

    private void SetZoom(double level)
    {
        var clamped = Math.Clamp(level, ZoomMin, ZoomMax);
        // No-op if we're already at the target (prevents a repaint on
        // held-key bounces when already at ZoomMax/ZoomMin).
        if (Math.Abs(clamped - _zoomLevel) < 0.001) return;
        _zoomLevel = clamped;
        ContentScale.ScaleX = _zoomLevel;
        ContentScale.ScaleY = _zoomLevel;
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

        UpdateContentViewportClip();
        _energyDrain = new SidebarEnergyDrainAnimator(EnergyCanvas);
        if (_activeNavButton is not null)
            PositionActiveBar(_activeNavButton);
    }

    private SidebarEnergyDrainAnimator? _energyDrain;
    private float _pulseTargetY = 120f;

    // ── UI Zoom ──────────────────────────────────────────────────────
    private const double ZoomStep = 0.05;
    private const double ZoomMin = 0.6;
    private const double ZoomMax = 1.6;
    private const int ZoomThrottleMs = 50;
    private double _zoomLevel = 1.0;
    private int _lastZoomTick;

    private void UpdateEnergyDrain()
    {
        var sidebarHeight = (float)SidebarRoot.ActualHeight;
        if (sidebarHeight <= 0) sidebarHeight = 700f;
        _energyDrain?.UpdateTarget(_pulseTargetY, sidebarHeight);
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
        }

        btn.Background = InactiveBg;
        btn.Foreground = ActiveFg;
        _activeNavButton = btn;

        PositionActiveBar(btn);
    }

    /// <summary>
    /// Update pulse target to the active nav button and restart pulse animations.
    /// </summary>
    private void PositionActiveBar(Button btn)
    {
        if (SidebarRoot is null) return;

        void Apply()
        {
            try
            {
                var transform = btn.TransformToVisual(SidebarRoot);
                var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                var btnHeight = btn.ActualHeight > 0 ? btn.ActualHeight : 44;
                _pulseTargetY = (float)(topLeft.Y + btnHeight / 2.0);
                UpdateEnergyDrain();
            }
            catch { }
        }

        if (btn.ActualHeight == 0)
        {
            EventHandler<object>? handler = null;
            handler = (_, _) =>
            {
                btn.LayoutUpdated -= handler;
                Apply();
            };
            btn.LayoutUpdated += handler;
        }
        else
        {
            Apply();
        }
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
    }

    private void OnContentViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateContentViewportClip();
    }

    private void UpdateContentViewportClip()
    {
        // No-op: clip removed to prevent content from being cut off at edges.
    }
}

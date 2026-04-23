#nullable enable

using System;
using Revu.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Revu.App.Controls;

/// <summary>
/// Mockup-aligned game row: thin glowing W/L bar on left, champion + meta + numbers,
/// gradient sweep + in-place depth hover. Replaces the heavier <see cref="GameCard"/>
/// for inbox/list views.
/// </summary>
[ContentProperty(Name = nameof(Actions))]
public sealed partial class GameRowCard : UserControl
{
    private const double HoverLiftY = 0;
    private const double HoverScale = 1.0;
    private bool _isHoverActive;

    public GameRowCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public static readonly DependencyProperty ChampionProperty =
        DependencyProperty.Register(
            nameof(Champion),
            typeof(string),
            typeof(GameRowCard),
            new PropertyMetadata("", (d, e) => ((GameRowCard)d).ChampionTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string Champion
    {
        get => (string)GetValue(ChampionProperty);
        set => SetValue(ChampionProperty, value);
    }

    public static readonly DependencyProperty MetaProperty =
        DependencyProperty.Register(
            nameof(Meta),
            typeof(string),
            typeof(GameRowCard),
            new PropertyMetadata("", (d, e) => ((GameRowCard)d).MetaTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string Meta
    {
        get => (string)GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public static readonly DependencyProperty KdaProperty =
        DependencyProperty.Register(
            nameof(Kda),
            typeof(string),
            typeof(GameRowCard),
            new PropertyMetadata("", (d, e) => ((GameRowCard)d).KdaTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string Kda
    {
        get => (string)GetValue(KdaProperty);
        set => SetValue(KdaProperty, value);
    }

    public static readonly DependencyProperty StatsProperty =
        DependencyProperty.Register(
            nameof(Stats),
            typeof(string),
            typeof(GameRowCard),
            new PropertyMetadata("", (d, e) => ((GameRowCard)d).StatsTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string Stats
    {
        get => (string)GetValue(StatsProperty);
        set => SetValue(StatsProperty, value);
    }

    public static readonly DependencyProperty WinProperty =
        DependencyProperty.Register(
            nameof(Win),
            typeof(bool),
            typeof(GameRowCard),
            new PropertyMetadata(false, (d, e) => ((GameRowCard)d).ApplyWinLoss((bool)e.NewValue)));

    public bool Win
    {
        get => (bool)GetValue(WinProperty);
        set => SetValue(WinProperty, value);
    }

    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(
            nameof(Actions),
            typeof(object),
            typeof(GameRowCard),
            new PropertyMetadata(null, (d, e) => ((GameRowCard)d).ActionsHost.Content = e.NewValue));

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWinLoss(Win);
        ResetHoverState();
    }

    private void ApplyWinLoss(bool win)
    {
        if (WinLossBar is null) return;
        var key = win ? "WinGreenBrush" : "LossRedBrush";
        WinLossBar.Fill = (Brush)Application.Current.Resources[key];
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ActivateHover(e.GetCurrentPoint(HostBorder).Position);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(HostBorder).Position;
        if (!_isHoverActive)
        {
            ActivateHover(position);
            return;
        }

        UpdateGlow(position);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        DeactivateHover();
    }

    private void ActivateHover(Point position)
    {
        _isHoverActive = true;
        HostBorder.Background = (Brush)Application.Current.Resources["CardHoverBackgroundBrush"];
        HostBorder.BorderBrush = (Brush)Application.Current.Resources["BrightBorderBrush"];
        AnimationHelper.AnimateOpacity(SweepRect, 1.0, 180);
        AnimationHelper.AnimateOpacity(GlowOverlay, 1.0, 220);
        AnimationHelper.AnimateOpacity(TopHighlight, 0.8, 180);
        AnimationHelper.AnimateOffset(HostBorder, 0.0, HoverLiftY, 180);
        AnimationHelper.AnimateScale(HostBorder, HostBorder, HoverScale, 180);
        UpdateGlow(position);
    }

    private void DeactivateHover()
    {
        _isHoverActive = false;
        HostBorder.Background = (Brush)Application.Current.Resources["CardBackgroundBrush"];
        HostBorder.BorderBrush = (Brush)Application.Current.Resources["SubtleBorderBrush"];
        AnimationHelper.AnimateOpacity(SweepRect, 0.0, 140);
        AnimationHelper.AnimateOpacity(GlowOverlay, 0.0, 120);
        AnimationHelper.AnimateOpacity(TopHighlight, 0.0, 120);
        AnimationHelper.AnimateOffset(HostBorder, 0.0, 0.0, 180);
        AnimationHelper.AnimateScale(HostBorder, HostBorder, 1.0, 180);
    }

    private void ResetHoverState()
    {
        _isHoverActive = false;
        HostBorder.Background = (Brush)Application.Current.Resources["CardBackgroundBrush"];
        HostBorder.BorderBrush = (Brush)Application.Current.Resources["SubtleBorderBrush"];
        AnimationHelper.SetOpacity(SweepRect, 0.0);
        AnimationHelper.SetOpacity(GlowOverlay, 0.0);
        AnimationHelper.SetOpacity(TopHighlight, 0.0);
        AnimationHelper.SetOffset(HostBorder, 0.0, 0.0);
        AnimationHelper.SetScale(HostBorder, HostBorder, 1.0);
    }

    private void UpdateGlow(Point position)
    {
        var normalized = NormalizePoint(position, HostBorder.ActualWidth, HostBorder.ActualHeight);
        GlowBrush.Center = normalized;
        GlowBrush.GradientOrigin = normalized;
    }

    private static Point NormalizePoint(Point position, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return new Point(0.5, 0.5);
        }

        return new Point(
            Math.Max(0.0, Math.Min(1.0, position.X / width)),
            Math.Max(0.0, Math.Min(1.0, position.Y / height)));
    }
}

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
/// Card surface that fades in four targeting-reticle corner brackets on hover.
/// Use as a drop-in replacement for Border + ElevatedCardStyle.
/// </summary>
[ContentProperty(Name = nameof(Content))]
public sealed partial class CornerBracketedCard : UserControl
{
    private const double HoverLiftY = -0.5;
    private const double HoverDepthZ = 3.0;
    private const double MaxTiltDegrees = 0.4;
    private bool _cornerHoverAttached;
    private bool _isHoverActive;
    private readonly HoverTiltController _hoverTilt;

    public CornerBracketedCard()
    {
        InitializeComponent();
        _hoverTilt = new HoverTiltController(HoverSurface, HoverSurface, MaxTiltDegrees, HoverLiftY, HoverDepthZ, 0.2);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public new static readonly DependencyProperty ContentProperty =
        DependencyProperty.Register(
            nameof(Content),
            typeof(object),
            typeof(CornerBracketedCard),
            new PropertyMetadata(null, (d, e) => ((CornerBracketedCard)d).ContentHost.Content = e.NewValue));

    public new object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public static readonly DependencyProperty CardPaddingProperty =
        DependencyProperty.Register(
            nameof(CardPadding),
            typeof(Thickness),
            typeof(CornerBracketedCard),
            new PropertyMetadata(new Thickness(20), (d, e) => ((CornerBracketedCard)d).MainBorder.Padding = (Thickness)e.NewValue));

    public Thickness CardPadding
    {
        get => (Thickness)GetValue(CardPaddingProperty);
        set => SetValue(CardPaddingProperty, value);
    }

    public static readonly DependencyProperty CardBackgroundProperty =
        DependencyProperty.Register(
            nameof(CardBackground),
            typeof(Brush),
            typeof(CornerBracketedCard),
            new PropertyMetadata(null, (d, e) =>
            {
                if (e.NewValue is Brush brush)
                {
                    ((CornerBracketedCard)d).MainBorder.Background = brush;
                }
            }));

    public Brush? CardBackground
    {
        get => (Brush?)GetValue(CardBackgroundProperty);
        set => SetValue(CardBackgroundProperty, value);
    }

    public static readonly DependencyProperty CardBorderBrushProperty =
        DependencyProperty.Register(
            nameof(CardBorderBrush),
            typeof(Brush),
            typeof(CornerBracketedCard),
            new PropertyMetadata(null, (d, e) =>
            {
                if (e.NewValue is Brush brush)
                {
                    ((CornerBracketedCard)d).MainBorder.BorderBrush = brush;
                }
            }));

    public Brush? CardBorderBrush
    {
        get => (Brush?)GetValue(CardBorderBrushProperty);
        set => SetValue(CardBorderBrushProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_cornerHoverAttached)
        {
            AnimationHelper.AttachCornerBracketsHover(HoverSurface, TopLeft, TopRight, BottomLeft, BottomRight);
            _cornerHoverAttached = true;
        }

        ResetHoverState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ResetHoverState();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ActivateHover(e.GetCurrentPoint(HoverSurface).Position);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(HoverSurface).Position;
        if (!_isHoverActive)
        {
            ActivateHover(position);
            return;
        }

        _hoverTilt.UpdatePointer(position);
        UpdateGlow(position);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        DeactivateHover();
    }

    private void ActivateHover(Point position)
    {
        _isHoverActive = true;
        MainBorder.BorderBrush = CardBorderBrush ?? (Brush)Application.Current.Resources["BrightBorderBrush"];
        Canvas.SetZIndex(this, 1);
        AnimationHelper.AnimateOpacity(GlowOverlay, 0.7, 220);
        _hoverTilt.UpdatePointer(position);
        UpdateGlow(position);
    }

    private void DeactivateHover()
    {
        _isHoverActive = false;
        MainBorder.BorderBrush = CardBorderBrush ?? (Brush)Application.Current.Resources["SubtleBorderBrush"];
        Canvas.SetZIndex(this, 0);
        AnimationHelper.AnimateOpacity(GlowOverlay, 0.0, 120);
        _hoverTilt.Relax();
    }

    private void ResetHoverState()
    {
        _isHoverActive = false;
        MainBorder.BorderBrush = CardBorderBrush ?? (Brush)Application.Current.Resources["SubtleBorderBrush"];
        Canvas.SetZIndex(this, 0);
        AnimationHelper.SetOpacity(GlowOverlay, 0.0);
        AnimationHelper.SetOpacity(TopLeft, 0.0);
        AnimationHelper.SetOpacity(TopRight, 0.0);
        AnimationHelper.SetOpacity(BottomLeft, 0.0);
        AnimationHelper.SetOpacity(BottomRight, 0.0);
        _hoverTilt.Reset();
    }

    private void UpdateGlow(Point position)
    {
        if (HoverSurface.ActualWidth <= 0 || HoverSurface.ActualHeight <= 0)
        {
            return;
        }

        var normalized = NormalizePoint(position, HoverSurface.ActualWidth, HoverSurface.ActualHeight);
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

#nullable enable

using System;
using LoLReview.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LoLReview.App.Controls;

public enum StatCellPosition
{
    First,
    Middle,
    Last,
    Only,
}

/// <summary>
/// Single stat cell used inside <see cref="StatStrip"/>. Connected-strip border
/// behaviour is driven by Position, set automatically by the parent strip.
/// </summary>
public sealed partial class StatCell : UserControl
{
    private const double HoverLiftY = -0.5;
    private const double HoverDepthZ = 4.0;
    private const double MaxTiltDegrees = 0.6;
    private bool _isHoverActive;
    private readonly HoverTiltController _hoverTilt;

    public StatCell()
    {
        InitializeComponent();
        _hoverTilt = new HoverTiltController(OuterBorder, OuterBorder, MaxTiltDegrees, HoverLiftY, HoverDepthZ, 0.2);
        ApplyPosition();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(StatCell),
            new PropertyMetadata("", (d, e) => ((StatCell)d).LabelTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(object),
            typeof(StatCell),
            new PropertyMetadata(null, (d, e) => ((StatCell)d).ValueTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ValueBrushProperty =
        DependencyProperty.Register(
            nameof(ValueBrush),
            typeof(Brush),
            typeof(StatCell),
            new PropertyMetadata(null, (d, e) =>
            {
                if (e.NewValue is Brush brush)
                {
                    ((StatCell)d).ValueTextBlock.Foreground = brush;
                }
            }));

    public Brush? ValueBrush
    {
        get => (Brush?)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public static readonly DependencyProperty SubProperty =
        DependencyProperty.Register(
            nameof(Sub),
            typeof(string),
            typeof(StatCell),
            new PropertyMetadata("", OnSubChanged));

    public string Sub
    {
        get => (string)GetValue(SubProperty);
        set => SetValue(SubProperty, value);
    }

    private static void OnSubChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cell = (StatCell)d;
        var text = e.NewValue?.ToString() ?? "";
        cell.SubTextBlock.Text = text;
        cell.SubTextBlock.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(
            nameof(Position),
            typeof(StatCellPosition),
            typeof(StatCell),
            new PropertyMetadata(StatCellPosition.Middle, (d, e) => ((StatCell)d).ApplyPosition()));

    public StatCellPosition Position
    {
        get => (StatCellPosition)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    private void ApplyPosition()
    {
        if (OuterBorder is null)
        {
            return;
        }

        switch (Position)
        {
            case StatCellPosition.First:
                OuterBorder.BorderThickness = new Thickness(1, 1, 0, 1);
                OuterBorder.CornerRadius = new CornerRadius(2, 0, 0, 2);
                break;
            case StatCellPosition.Last:
                OuterBorder.BorderThickness = new Thickness(1, 1, 1, 1);
                OuterBorder.CornerRadius = new CornerRadius(0, 2, 2, 0);
                break;
            case StatCellPosition.Only:
                OuterBorder.BorderThickness = new Thickness(1);
                OuterBorder.CornerRadius = new CornerRadius(2);
                break;
            default:
                OuterBorder.BorderThickness = new Thickness(1, 1, 0, 1);
                OuterBorder.CornerRadius = new CornerRadius(0);
                break;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResetHoverState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ResetHoverState();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ActivateHover(e.GetCurrentPoint(OuterBorder).Position);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(OuterBorder).Position;
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
        OuterBorder.BorderBrush = (Brush)Application.Current.Resources["BrightBorderBrush"];
        Canvas.SetZIndex(this, 1);
        AnimationHelper.AnimateOpacity(AccentLine, 0.35, 180);
        AnimationHelper.AnimateOpacity(GlowOverlay, 1.0, 220);
        _hoverTilt.UpdatePointer(position);
        UpdateGlow(position);
    }

    private void DeactivateHover()
    {
        _isHoverActive = false;
        OuterBorder.BorderBrush = (Brush)Application.Current.Resources["SubtleBorderBrush"];
        Canvas.SetZIndex(this, 0);
        AnimationHelper.AnimateOpacity(AccentLine, 0.0, 140);
        AnimationHelper.AnimateOpacity(GlowOverlay, 0.0, 120);
        _hoverTilt.Relax();
    }

    private void ResetHoverState()
    {
        _isHoverActive = false;
        OuterBorder.BorderBrush = (Brush)Application.Current.Resources["SubtleBorderBrush"];
        Canvas.SetZIndex(this, 0);
        AnimationHelper.SetOpacity(AccentLine, 0.0);
        AnimationHelper.SetOpacity(GlowOverlay, 0.0);
        _hoverTilt.Reset();
    }

    private void UpdateGlow(Point position)
    {
        if (OuterBorder.ActualWidth <= 0 || OuterBorder.ActualHeight <= 0)
        {
            return;
        }

        var normalized = NormalizePoint(position, OuterBorder.ActualWidth, OuterBorder.ActualHeight);
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

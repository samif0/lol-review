#nullable enable

using System;
using Revu.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Revu.App.Controls;

/// <summary>
/// Circular progress ring with violet stroke + breathing glow halo.
/// Draws via an <see cref="Ellipse"/> with <c>StrokeDashArray</c>/<c>StrokeDashOffset</c>
/// so the arc grows-in on load (mirrors the mockup's stroke-dashoffset keyframes).
/// Named "HudProgressRing" to avoid colliding with Microsoft.UI.Xaml.Controls.ProgressRing.
/// </summary>
public sealed partial class HudProgressRing : UserControl
{
    // Ring geometry: 50px diameter, stroke width 3 → radius 25.
    // Circumference = 2πr ≈ 157.08 px.
    // WinUI's Shape.StrokeDashArray values are in MULTIPLES of StrokeThickness,
    // but StrokeDashOffset is in raw pixels. So the dash array gets the scaled
    // circumference (CircumferenceDash) and the offset gets the raw pixel
    // circumference (CircumferencePx). Mixing them up was causing 0% to render
    // as a ~2/3-filled ring (offset of ~52px out of 157px circumference).
    private const double Radius = 25.0;
    private const double StrokeThickness = 3.0;
    private const double CircumferencePx = 2.0 * Math.PI * Radius;
    private const double CircumferenceDash = CircumferencePx / StrokeThickness;

    private bool _drawInPlayed;

    public HudProgressRing()
    {
        InitializeComponent();
        // v2.15.10: dash array MUST be set before any StrokeDashOffset write,
        // otherwise the ellipse renders as a solid ring (offset has no effect
        // without a dash pattern). DP value-changed callbacks fire BEFORE the
        // first Loaded event, so deferring this to OnLoaded made the ring
        // briefly render at 100% on every page entry.
        ArcEllipse.StrokeDashArray.Add(CircumferenceDash);
        ArcEllipse.StrokeDashArray.Add(CircumferenceDash);
        Loaded += OnLoaded;
    }

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(HudProgressRing),
            new PropertyMetadata(0d, OnProgressChanged));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(HudProgressRing),
            new PropertyMetadata("", (d, e) => ((HudProgressRing)d).LabelText.Text = e.NewValue?.ToString() ?? ""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(HudProgressRing),
            new PropertyMetadata(null, (d, e) =>
            {
                if (e.NewValue is Brush b)
                {
                    var ring = (HudProgressRing)d;
                    ring.ArcEllipse.Stroke = b;
                    ring.LabelText.Foreground = b;
                }
            }));

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(
            nameof(TrackBrush),
            typeof(Brush),
            typeof(HudProgressRing),
            new PropertyMetadata(null, (d, e) =>
            {
                if (e.NewValue is Brush b)
                {
                    ((HudProgressRing)d).TrackEllipse.Stroke = b;
                }
            }));

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HudProgressRing)d).UpdateArc(animate: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_drawInPlayed)
        {
            _drawInPlayed = true;
            UpdateArc(animate: true);
        }
        else
        {
            UpdateArc(animate: false);
        }

        AnimationHelper.AttachPulseOpacity(ArcEllipse, 0.75, 1.0, 3.0);
    }

    private void UpdateArc(bool animate)
    {
        if (ArcEllipse is null) return;

        var ratio = double.IsFinite(Progress) ? Math.Clamp(Progress, 0.0, 1.0) : 0.0;
        // Offset is in raw pixels. ratio=0 → fully hidden (offset = full px circumference),
        // ratio=1 → fully revealed (offset = 0).
        var targetOffset = CircumferencePx * (1.0 - ratio);

        if (!animate || !IsLoaded)
        {
            ArcEllipse.StrokeDashOffset = targetOffset;
            return;
        }

        // Animate from "empty" (full circumference offset) to the target.
        var anim = new DoubleAnimation
        {
            From = CircumferencePx,
            To = targetOffset,
            Duration = new Duration(TimeSpan.FromMilliseconds(1200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var sb = new Storyboard();
        Storyboard.SetTarget(anim, ArcEllipse);
        Storyboard.SetTargetProperty(anim, "(Shape.StrokeDashOffset)");
        sb.Children.Add(anim);
        sb.Begin();
    }
}

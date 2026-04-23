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
    // not absolute pixels. Using the raw pixel circumference makes the dash
    // "longer than the circle can render," so the stroke appears fully drawn
    // regardless of offset. Scale by stroke thickness to fix.
    private const double Radius = 25.0;
    private const double StrokeThickness = 3.0;
    private const double CircumferencePx = 2.0 * Math.PI * Radius;
    private const double Circumference = CircumferencePx / StrokeThickness;

    private bool _drawInPlayed;

    public HudProgressRing()
    {
        InitializeComponent();
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
        // Initialize dash array once — a single dash the length of the circumference,
        // then use offset to reveal the desired arc portion.
        if (ArcEllipse.StrokeDashArray.Count == 0)
        {
            ArcEllipse.StrokeDashArray.Add(Circumference);
            ArcEllipse.StrokeDashArray.Add(Circumference);  // gap same length so only the dashed part shows
        }

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
        var targetOffset = Circumference * (1.0 - ratio);

        if (!animate || !IsLoaded)
        {
            ArcEllipse.StrokeDashOffset = targetOffset;
            return;
        }

        // Animate from "empty" (full circumference offset) to the target.
        var anim = new DoubleAnimation
        {
            From = Circumference,
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

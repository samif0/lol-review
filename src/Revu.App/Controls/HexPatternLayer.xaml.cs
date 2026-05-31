#nullable enable

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Revu.App.Controls;

/// <summary>
/// Draws a clipped honeycomb field without creating a large tree of Polygon elements.
/// The parent control only toggles opacity; the pattern itself is a single geometry.
/// </summary>
public sealed partial class HexPatternLayer : UserControl
{
    private const double OverscanPx = 0.0;

    // Previously: static Dictionary<string, Geometry> shared across layers.
    // That crashes under "System.ArgumentException: Value does not fall
    // within the expected range" because a WinUI Geometry can only belong
    // to one Path at a time — assigning a shared cached Geometry to a
    // second layer's Path.Data throws. On heavy reparenting scenarios
    // (Ctrl+/- zoom, pointer moves hitting many CornerBracketedCards at
    // once) this surfaced as a rapid-fire exception storm that could hang
    // the UI thread or trip display driver recovery on weaker GPUs.
    //
    // Each instance now builds its own PathGeometry. The build itself is
    // pure CPU (no GPU involvement) and small (<1 ms for typical sizes),
    // so the cache wasn't earning its keep anyway.

    private bool _patternDirty = true;
    private Size _lastBuiltSize;

    public HexPatternLayer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        // LayoutUpdated was previously subscribed here but fires on every layout
        // pass app-wide, causing unnecessary work. SizeChanged is sufficient to
        // detect real size changes; EnsurePattern() handles the deferred-build
        // path when size isn't available yet at construction time.
    }

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(HexPatternLayer),
            new PropertyMetadata(null, OnPatternPropertyChanged));

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public static readonly DependencyProperty HexSizeProperty =
        DependencyProperty.Register(
            nameof(HexSize),
            typeof(double),
            typeof(HexPatternLayer),
            new PropertyMetadata(10.0, OnPatternPropertyChanged));

    public double HexSize
    {
        get => (double)GetValue(HexSizeProperty);
        set => SetValue(HexSizeProperty, value);
    }

    public static readonly DependencyProperty FillOpacityProperty =
        DependencyProperty.Register(
            nameof(FillOpacity),
            typeof(double),
            typeof(HexPatternLayer),
            new PropertyMetadata(0.0, OnPatternPropertyChanged));

    public double FillOpacity
    {
        get => (double)GetValue(FillOpacityProperty);
        set => SetValue(FillOpacityProperty, value);
    }

    public static readonly DependencyProperty StrokeOpacityProperty =
        DependencyProperty.Register(
            nameof(StrokeOpacity),
            typeof(double),
            typeof(HexPatternLayer),
            new PropertyMetadata(0.05, OnPatternPropertyChanged));

    public double StrokeOpacity
    {
        get => (double)GetValue(StrokeOpacityProperty);
        set => SetValue(StrokeOpacityProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(HexPatternLayer),
            new PropertyMetadata(0.85, OnPatternPropertyChanged));

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public static readonly DependencyProperty OffsetXProperty =
        DependencyProperty.Register(
            nameof(OffsetX),
            typeof(double),
            typeof(HexPatternLayer),
            new PropertyMetadata(0.0, OnOffsetChanged));

    public double OffsetX
    {
        get => (double)GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    public static readonly DependencyProperty OffsetYProperty =
        DependencyProperty.Register(
            nameof(OffsetY),
            typeof(double),
            typeof(HexPatternLayer),
            new PropertyMetadata(0.0, OnOffsetChanged));

    public double OffsetY
    {
        get => (double)GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    private static void OnPatternPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HexPatternLayer)d).InvalidatePattern();
    }

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HexPatternLayer)d).ApplyOffset();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InvalidatePattern();
        // Build now if we already have an arranged size. Covers the case where
        // SizeChanged fired before Loaded (already-sized parent), since we no
        // longer have the app-wide LayoutUpdated fallback to catch a late build.
        EnsurePattern();
        ApplyOffset();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        PatternPath.Data = null;
        _lastBuiltSize = default;
        InvalidatePattern();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Skip rebuild when the size delta is negligible (sub-4px in both axes).
        // This suppresses spurious calls during border animations, DPI rounding,
        // and panel micro-adjustments that would never visibly change the pattern.
        const double MinDeltaPx = 4.0;
        var newSize = e.NewSize;
        if (Math.Abs(newSize.Width - _lastBuiltSize.Width) < MinDeltaPx &&
            Math.Abs(newSize.Height - _lastBuiltSize.Height) < MinDeltaPx)
        {
            return;
        }

        InvalidatePattern();
        // If we already have a valid size, rebuild immediately so callers don't
        // need to call EnsurePattern() after every resize.
        if (newSize.Width > 0 && newSize.Height > 0)
        {
            RebuildPattern();
        }
    }

    public void EnsurePattern()
    {
        if (_patternDirty || PatternPath.Data is null)
        {
            RebuildPattern();
        }
    }

    private void InvalidatePattern()
    {
        _patternDirty = true;
    }

    private void RebuildPattern()
    {
        if (LayoutRoot.ActualWidth <= 0 || LayoutRoot.ActualHeight <= 0)
        {
            return;
        }

        var hexRadius = Math.Max(7.0, HexSize);
        var hexWidth = hexRadius * 2.0;
        var hexHeight = Math.Sqrt(3.0) * hexRadius;
        var columnStep = hexRadius * 1.5;
        var rowStep = hexHeight;
        var totalWidth = LayoutRoot.ActualWidth + (OverscanPx * 2.0);
        var totalHeight = LayoutRoot.ActualHeight + (OverscanPx * 2.0);

        // Guard against degenerate sizes (e.g. during window minimize or
        // mid-animation collapse). A zero-or-negative box would still
        // produce a valid empty PathGeometry, but we'd rather skip the
        // work entirely and wait for the next layout pass.
        if (totalWidth <= 0 || totalHeight <= 0 || !double.IsFinite(totalWidth) || !double.IsFinite(totalHeight))
        {
            return;
        }

        LayoutRoot.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, LayoutRoot.ActualWidth, LayoutRoot.ActualHeight)
        };

        var accentColor = ResolveAccentColor();
        PatternCanvas.Width = LayoutRoot.ActualWidth;
        PatternCanvas.Height = LayoutRoot.ActualHeight;
        Canvas.SetLeft(PatternPath, -OverscanPx);
        Canvas.SetTop(PatternPath, -OverscanPx);
        PatternPath.Fill = CreatePatternBrush(accentColor, FillOpacity);
        PatternPath.Stroke = CreatePatternBrush(accentColor, StrokeOpacity);
        PatternPath.StrokeThickness = StrokeThickness;

        // Build a fresh geometry per instance — see class-level comment
        // on why sharing is unsafe. Wrap in a try/catch because even a
        // valid-looking PathGeometry can be rejected by the compositor
        // under extreme transform states; better to quietly skip a frame
        // than to throw into a pointer/key handler.
        try
        {
            PatternPath.Data = BuildPatternGeometry(totalWidth, totalHeight, hexWidth, hexHeight, columnStep, rowStep);
        }
        catch (Exception)
        {
            return;
        }

        _patternDirty = false;
        _lastBuiltSize = new Size(LayoutRoot.ActualWidth, LayoutRoot.ActualHeight);

        ApplyOffset();
    }

    private void ApplyOffset()
    {
        PatternTransform.X = OffsetX;
        PatternTransform.Y = OffsetY;
    }

    private Color ResolveAccentColor()
    {
        if (AccentBrush is SolidColorBrush accent)
        {
            return accent.Color;
        }

        try
        {
            if (Application.Current.Resources["AccentBlueBrush"] is SolidColorBrush themeAccent)
            {
                return themeAccent.Color;
            }
        }
        catch
        {
        }

        return Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA);
    }

    private static Brush? CreatePatternBrush(Color color, double opacity)
    {
        if (opacity <= 0)
        {
            return null;
        }

        var clampedOpacity = Math.Max(0.0, Math.Min(1.0, opacity));
        return new SolidColorBrush(WithOpacity(color, clampedOpacity));
    }

    private static Color WithOpacity(Color color, double opacity)
    {
        var alpha = (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, opacity)) * 255.0);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static PointCollection CreateHexPoints(double hexWidth, double hexHeight)
    {
        return new PointCollection
        {
            new Point(hexWidth * 0.25, 0),
            new Point(hexWidth * 0.75, 0),
            new Point(hexWidth, hexHeight * 0.5),
            new Point(hexWidth * 0.75, hexHeight),
            new Point(hexWidth * 0.25, hexHeight),
            new Point(0, hexHeight * 0.5)
        };
    }

    private static Geometry BuildPatternGeometry(
        double totalWidth,
        double totalHeight,
        double hexWidth,
        double hexHeight,
        double columnStep,
        double rowStep)
    {
        var points = CreateHexPoints(hexWidth, hexHeight);
        var figures = new PathFigureCollection();
        var columns = (int)Math.Ceiling(totalWidth / columnStep) + 1;
        var rows = (int)Math.Ceiling(totalHeight / rowStep) + 1;

        for (var column = 0; column < columns; column++)
        {
            var x = column * columnStep;
            var yOffset = (column & 1) == 0 ? 0.0 : hexHeight / 2.0;

            for (var row = 0; row < rows; row++)
            {
                var y = (row * rowStep) + yOffset;
                var segments = new PathSegmentCollection();

                for (var index = 1; index < points.Count; index++)
                {
                    segments.Add(new LineSegment
                    {
                        Point = new Point(x + points[index].X, y + points[index].Y)
                    });
                }

                figures.Add(new PathFigure
                {
                    StartPoint = new Point(x + points[0].X, y + points[0].Y),
                    IsClosed = true,
                    IsFilled = true,
                    Segments = segments
                });
            }
        }

        return new PathGeometry
        {
            Figures = figures
        };
    }
}

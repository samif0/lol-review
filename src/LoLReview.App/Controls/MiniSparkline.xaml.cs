#nullable enable

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LoLReview.App.Controls;

/// <summary>
/// Tiny score-over-time sparkline drawn as a polyline on a Canvas.
/// Bind <see cref="DataPoints"/> to an IReadOnlyList&lt;int&gt; of cumulative scores.
/// </summary>
public sealed partial class MiniSparkline : UserControl
{
    public static readonly DependencyProperty DataPointsProperty =
        DependencyProperty.Register(
            nameof(DataPoints),
            typeof(IReadOnlyList<int>),
            typeof(MiniSparkline),
            new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty LineColorProperty =
        DependencyProperty.Register(
            nameof(LineColor),
            typeof(Brush),
            typeof(MiniSparkline),
            new PropertyMetadata(new SolidColorBrush(ColorHelper.FromArgb(255, 201, 149, 106)), OnDataChanged)); // #C9956A bronze

    public static readonly DependencyProperty LineThicknessProperty =
        DependencyProperty.Register(
            nameof(LineThickness),
            typeof(double),
            typeof(MiniSparkline),
            new PropertyMetadata(1.5, OnDataChanged));

    public IReadOnlyList<int>? DataPoints
    {
        get => (IReadOnlyList<int>?)GetValue(DataPointsProperty);
        set => SetValue(DataPointsProperty, value);
    }

    public Brush LineColor
    {
        get => (Brush)GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public double LineThickness
    {
        get => (double)GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    public MiniSparkline()
    {
        InitializeComponent();
        Loaded += (_, _) => Redraw();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MiniSparkline)d).Redraw();

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        SparkCanvas.Children.Clear();

        var points = DataPoints;
        if (points is null || points.Count < 2)
            return;

        var w = SparkCanvas.ActualWidth;
        var h = SparkCanvas.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var min = (double)points.Min();
        var max = (double)points.Max();
        var range = max - min;
        if (range < 1) range = 1; // avoid divide-by-zero on flat lines

        var polyline = new Polyline
        {
            Stroke = LineColor,
            StrokeThickness = LineThickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        var padding = LineThickness;
        var drawH = h - padding * 2;
        var drawW = w - padding * 2;
        var step = drawW / (points.Count - 1);

        for (int i = 0; i < points.Count; i++)
        {
            var x = padding + i * step;
            var y = padding + drawH - (points[i] - min) / range * drawH;
            polyline.Points.Add(new Windows.Foundation.Point(x, y));
        }

        SparkCanvas.Children.Add(polyline);

        // Draw a subtle end-dot
        var last = polyline.Points[^1];
        var dot = new Ellipse
        {
            Width = LineThickness * 2.5,
            Height = LineThickness * 2.5,
            Fill = LineColor
        };
        Canvas.SetLeft(dot, last.X - dot.Width / 2);
        Canvas.SetTop(dot, last.Y - dot.Height / 2);
        SparkCanvas.Children.Add(dot);
    }
}

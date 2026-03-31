#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.Controls;

/// <summary>
/// Lightweight determinate progress bar that does not depend on the default WinUI ProgressBar template.
/// </summary>
public sealed partial class ObjectiveProgressBar : UserControl
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(ObjectiveProgressBar),
            new PropertyMetadata(0d, OnAppearancePropertyChanged));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(
            nameof(FillBrush),
            typeof(Brush),
            typeof(ObjectiveProgressBar),
            new PropertyMetadata(null, OnAppearancePropertyChanged));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(
            nameof(TrackBrush),
            typeof(Brush),
            typeof(ObjectiveProgressBar),
            new PropertyMetadata(null, OnAppearancePropertyChanged));

    public static readonly DependencyProperty BarHeightProperty =
        DependencyProperty.Register(
            nameof(BarHeight),
            typeof(double),
            typeof(ObjectiveProgressBar),
            new PropertyMetadata(8d, OnAppearancePropertyChanged));

    public static readonly DependencyProperty BarCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(BarCornerRadius),
            typeof(CornerRadius),
            typeof(ObjectiveProgressBar),
            new PropertyMetadata(new CornerRadius(4), OnAppearancePropertyChanged));

    public static readonly DependencyProperty MinimumVisibleFillWidthProperty =
        DependencyProperty.Register(
            nameof(MinimumVisibleFillWidth),
            typeof(double),
            typeof(ObjectiveProgressBar),
            new PropertyMetadata(4d, OnAppearancePropertyChanged));

    public ObjectiveProgressBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double BarHeight
    {
        get => (double)GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    public CornerRadius BarCornerRadius
    {
        get => (CornerRadius)GetValue(BarCornerRadiusProperty);
        set => SetValue(BarCornerRadiusProperty, value);
    }

    public double MinimumVisibleFillWidth
    {
        get => (double)GetValue(MinimumVisibleFillWidthProperty);
        set => SetValue(MinimumVisibleFillWidthProperty, value);
    }

    private static void OnAppearancePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ObjectiveProgressBar)d).UpdateVisuals();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateVisuals();
    }

    private void OnLayoutRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (TrackBorder is null || FillBorder is null || LayoutRoot is null)
        {
            return;
        }

        TrackBorder.Height = BarHeight;
        TrackBorder.CornerRadius = BarCornerRadius;
        TrackBorder.Background = TrackBrush;

        FillBorder.Height = BarHeight;
        FillBorder.CornerRadius = BarCornerRadius;
        FillBorder.Background = FillBrush;

        var ratio = double.IsFinite(Progress) ? Math.Clamp(Progress, 0d, 1d) : 0d;
        var totalWidth = LayoutRoot.ActualWidth;
        if (totalWidth <= 0)
        {
            FillBorder.Width = 0;
            return;
        }

        if (ratio <= 0)
        {
            FillBorder.Width = 0;
            FillBorder.Visibility = Visibility.Collapsed;
            return;
        }

        FillBorder.Visibility = Visibility.Visible;

        var targetWidth = ratio * totalWidth;
        var minimumWidth = Math.Max(0, MinimumVisibleFillWidth);
        FillBorder.Width = ratio >= 1d
            ? totalWidth
            : Math.Min(totalWidth, Math.Max(targetWidth, minimumWidth));
    }
}

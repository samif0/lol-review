#nullable enable

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using LoLReview.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace LoLReview.App.Controls;

/// <summary>
/// Interactive timeline with event markers, progress bar, and clip range overlay.
/// Ported from the Python TimelineCanvas.
/// </summary>
public sealed partial class TimelineControl : UserControl
{
    private const double TrackTop = 16;
    private const double TrackHeight = 8;
    private const double MarkerSize = 7;
    private const double Padding = 8;
    private const string BookmarkColor = "#8b5cf6";

    private bool _isDragging;
    private readonly List<MarkerHitInfo> _markerHitAreas = new();

    public TimelineControl()
    {
        InitializeComponent();
    }

    // ── Dependency Properties ───────────────────────────────────────

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(double), typeof(TimelineControl),
            new PropertyMetadata(1.0, OnLayoutPropertyChanged));

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(double), typeof(TimelineControl),
            new PropertyMetadata(0.0, OnPositionChanged));

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public static readonly DependencyProperty EventsProperty =
        DependencyProperty.Register(nameof(Events), typeof(ObservableCollection<TimelineEvent>),
            typeof(TimelineControl), new PropertyMetadata(null, OnEventsChanged));

    public ObservableCollection<TimelineEvent>? Events
    {
        get => (ObservableCollection<TimelineEvent>?)GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
    }

    public static readonly DependencyProperty BookmarksProperty =
        DependencyProperty.Register(nameof(Bookmarks), typeof(ObservableCollection<BookmarkItem>),
            typeof(TimelineControl), new PropertyMetadata(null, OnEventsChanged));

    public ObservableCollection<BookmarkItem>? Bookmarks
    {
        get => (ObservableCollection<BookmarkItem>?)GetValue(BookmarksProperty);
        set => SetValue(BookmarksProperty, value);
    }

    public static readonly DependencyProperty DerivedEventsProperty =
        DependencyProperty.Register(nameof(DerivedEvents), typeof(ObservableCollection<DerivedEventRegion>),
            typeof(TimelineControl), new PropertyMetadata(null, OnEventsChanged));

    public ObservableCollection<DerivedEventRegion>? DerivedEvents
    {
        get => (ObservableCollection<DerivedEventRegion>?)GetValue(DerivedEventsProperty);
        set => SetValue(DerivedEventsProperty, value);
    }

    /// <summary>Clip start in seconds. Negative means no clip start set.</summary>
    public static readonly DependencyProperty ClipStartProperty =
        DependencyProperty.Register(nameof(ClipStart), typeof(double), typeof(TimelineControl),
            new PropertyMetadata(-1.0, OnLayoutPropertyChanged));

    public double ClipStart
    {
        get => (double)GetValue(ClipStartProperty);
        set => SetValue(ClipStartProperty, value);
    }

    /// <summary>Clip end in seconds. Negative means no clip end set.</summary>
    public static readonly DependencyProperty ClipEndProperty =
        DependencyProperty.Register(nameof(ClipEnd), typeof(double), typeof(TimelineControl),
            new PropertyMetadata(-1.0, OnLayoutPropertyChanged));

    public double ClipEnd
    {
        get => (double)GetValue(ClipEndProperty);
        set => SetValue(ClipEndProperty, value);
    }

    // ── Events ──────────────────────────────────────────────────────

    public event Action<double>? SeekRequested;

    // ── Property change callbacks ───────────────────────────────────

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl tc && !tc._isDragging)
            tc.UpdateProgressBar();
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl tc)
            tc.Redraw();
    }

    private static void OnEventsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TimelineControl tc) return;

        // Unsubscribe from old collection
        if (e.OldValue is INotifyCollectionChanged oldColl)
            oldColl.CollectionChanged -= tc.OnCollectionChanged;

        // Subscribe to new collection
        if (e.NewValue is INotifyCollectionChanged newColl)
            newColl.CollectionChanged += tc.OnCollectionChanged;

        tc.Redraw();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    // ── Sizing ──────────────────────────────────────────────────────

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // ── Rendering ───────────────────────────────────────────────────

    private double TrackWidth => Math.Max(1, ActualWidth - 2 * Padding);

    private double TimeToX(double seconds)
    {
        var dur = Math.Max(Duration, 1);
        return Padding + (seconds / dur) * TrackWidth;
    }

    private double XToTime(double x)
    {
        var dur = Math.Max(Duration, 1);
        var ratio = Math.Clamp((x - Padding) / TrackWidth, 0, 1);
        return ratio * dur;
    }

    private void Redraw()
    {
        if (ActualWidth <= 0) return;

        // Track background
        TrackBg.Width = TrackWidth;
        Canvas.SetLeft(TrackBg, Padding);

        // Clip overlay
        UpdateClipOverlay();

        // Progress bar
        UpdateProgressBar();

        // Markers
        RedrawMarkers();
    }

    private void UpdateProgressBar()
    {
        if (ActualWidth <= 0) return;
        var dur = Math.Max(Duration, 1);
        var ratio = Math.Clamp(Position / dur, 0, 1);
        ProgressBar.Width = Math.Max(0, ratio * TrackWidth);
        Canvas.SetLeft(ProgressBar, Padding);
    }

    private void UpdateClipOverlay()
    {
        // In-marker
        if (ClipStart >= 0)
        {
            Canvas.SetLeft(ClipInMarker, TimeToX(ClipStart) - 1);
            ClipInMarker.Visibility = Visibility.Visible;
        }
        else
        {
            ClipInMarker.Visibility = Visibility.Collapsed;
        }

        // Out-marker
        if (ClipEnd >= 0)
        {
            Canvas.SetLeft(ClipOutMarker, TimeToX(ClipEnd) - 1);
            ClipOutMarker.Visibility = Visibility.Visible;
        }
        else
        {
            ClipOutMarker.Visibility = Visibility.Collapsed;
        }

        // Range overlay — only when both are set
        if (ClipStart >= 0 && ClipEnd >= 0)
        {
            var startX = TimeToX(Math.Min(ClipStart, ClipEnd));
            var endX = TimeToX(Math.Max(ClipStart, ClipEnd));
            Canvas.SetLeft(ClipOverlay, startX);
            ClipOverlay.Width = Math.Max(2, endX - startX);
            ClipOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            ClipOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void RedrawMarkers()
    {
        MarkerCanvas.Children.Clear();
        _markerHitAreas.Clear();

        // Derived event regions (draw first, behind markers)
        if (DerivedEvents != null)
        {
            foreach (var de in DerivedEvents)
            {
                var x1 = TimeToX(de.StartTimeS);
                var x2 = TimeToX(de.EndTimeS);
                var rect = new Border
                {
                    Width = Math.Max(2, x2 - x1),
                    Height = TrackHeight + 4,
                    Background = new SolidColorBrush(ParseColor(de.Color, 80)),
                    CornerRadius = new CornerRadius(2),
                };
                Canvas.SetLeft(rect, x1);
                Canvas.SetTop(rect, TrackTop - 2);
                MarkerCanvas.Children.Add(rect);
            }
        }

        // Game event markers
        if (Events != null)
        {
            foreach (var evt in Events)
            {
                var x = TimeToX(evt.GameTimeS);
                var shape = CreateMarkerShape(evt.Shape, evt.Color);
                Canvas.SetLeft(shape, x - MarkerSize / 2);
                Canvas.SetTop(shape, TrackTop - MarkerSize - 2);
                MarkerCanvas.Children.Add(shape);

                _markerHitAreas.Add(new MarkerHitInfo
                {
                    X = x,
                    Label = $"{VodPlayerViewModel.FormatTime((int)evt.GameTimeS)} {evt.Label}",
                });
            }
        }

        // Bookmark markers (purple diamonds)
        if (Bookmarks != null)
        {
            foreach (var bm in Bookmarks)
            {
                var x = TimeToX(bm.GameTimeS);
                var shape = CreateMarkerShape(MarkerShape.Diamond, BookmarkColor);
                Canvas.SetLeft(shape, x - MarkerSize / 2);
                Canvas.SetTop(shape, TrackTop + TrackHeight + 2);
                MarkerCanvas.Children.Add(shape);

                var label = string.IsNullOrEmpty(bm.Note)
                    ? $"{bm.TimeText} Bookmark"
                    : $"{bm.TimeText} {bm.Note}";
                if (bm.IsClip) label += " [CLIP]";

                _markerHitAreas.Add(new MarkerHitInfo { X = x, Label = label });
            }
        }
    }

    private static FrameworkElement CreateMarkerShape(MarkerShape shape, string colorHex)
    {
        var color = ParseColor(colorHex);
        var brush = new SolidColorBrush(color);
        var size = MarkerSize;

        switch (shape)
        {
            case ViewModels.MarkerShape.TriangleUp:
            {
                var poly = new Polygon
                {
                    Points = { new Point(size / 2, 0), new Point(size, size), new Point(0, size) },
                    Fill = brush,
                    Width = size,
                    Height = size,
                };
                return poly;
            }
            case ViewModels.MarkerShape.TriangleDown:
            {
                var poly = new Polygon
                {
                    Points = { new Point(0, 0), new Point(size, 0), new Point(size / 2, size) },
                    Fill = brush,
                    Width = size,
                    Height = size,
                };
                return poly;
            }
            case ViewModels.MarkerShape.Diamond:
            {
                var poly = new Polygon
                {
                    Points =
                    {
                        new Point(size / 2, 0),
                        new Point(size, size / 2),
                        new Point(size / 2, size),
                        new Point(0, size / 2),
                    },
                    Fill = brush,
                    Width = size,
                    Height = size,
                };
                return poly;
            }
            case ViewModels.MarkerShape.Square:
            {
                var rect = new Border
                {
                    Width = size - 1,
                    Height = size - 1,
                    Background = brush,
                };
                return rect;
            }
            case ViewModels.MarkerShape.Star:
            {
                // Simplified star as a larger circle with glow
                var ell = new Ellipse
                {
                    Width = size + 2,
                    Height = size + 2,
                    Fill = brush,
                };
                return ell;
            }
            default: // Circle
            {
                var ell = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = brush,
                };
                return ell;
            }
        }
    }

    // ── Pointer handling ────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = true;
        CapturePointer(e.Pointer);
        var pos = e.GetCurrentPoint(this).Position;
        var time = XToTime(pos.X);
        // Update bar immediately so it doesn't wait for the 250ms position timer
        var dur = Math.Max(Duration, 1);
        ProgressBar.Width = Math.Max(0, Math.Clamp(time / dur, 0, 1) * TrackWidth);
        SeekRequested?.Invoke(time);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(this).Position;

        if (_isDragging)
        {
            var time = XToTime(pos.X);
            var dur = Math.Max(Duration, 1);
            ProgressBar.Width = Math.Max(0, Math.Clamp(time / dur, 0, 1) * TrackWidth);
            SeekRequested?.Invoke(time);
        }
        else
        {
            // Hover tooltip
            UpdateTooltip(pos);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        ReleasePointerCapture(e.Pointer);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        Tooltip.Visibility = Visibility.Collapsed;
    }

    private void UpdateTooltip(Point pos)
    {
        const double hitRadius = 10;

        MarkerHitInfo? closest = null;
        double closestDist = double.MaxValue;

        foreach (var info in _markerHitAreas)
        {
            var dist = Math.Abs(pos.X - info.X);
            if (dist < hitRadius && dist < closestDist)
            {
                closest = info;
                closestDist = dist;
            }
        }

        if (closest != null)
        {
            TooltipText.Text = closest.Label;
            Canvas.SetLeft(Tooltip, Math.Max(0, closest.X - 40));
            Tooltip.Visibility = Visibility.Visible;
        }
        else
        {
            // Show time tooltip when hovering the track area
            var time = XToTime(pos.X);
            if (time >= 0 && time <= Duration)
            {
                TooltipText.Text = VodPlayerViewModel.FormatTime((int)time);
                Canvas.SetLeft(Tooltip, Math.Max(0, pos.X - 20));
                Tooltip.Visibility = Visibility.Visible;
            }
            else
            {
                Tooltip.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static Windows.UI.Color ParseColor(string hex, byte alpha = 255)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            return Windows.UI.Color.FromArgb(alpha, r, g, b);
        }
        return Windows.UI.Color.FromArgb(alpha, 112, 112, 160); // fallback
    }

    private class MarkerHitInfo
    {
        public double X { get; set; }
        public string Label { get; set; } = "";
    }
}

#nullable enable

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Revu.App.Styling;
using Revu.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Revu.App.Controls;

/// <summary>
/// Interactive timeline with event markers, progress bar, and clip range overlay.
/// Ported from the Python TimelineCanvas.
/// </summary>
public sealed partial class TimelineControl : UserControl
{
    private const double TrackTop = 34;
    private const double TrackHeight = 12;
    private const double MarkerSize = 6;
    private const double TrackPadding = 16;
    private const double EventMarkerTop = TrackTop - MarkerSize - 10;
    private const double BookmarkMarkerTop = TrackTop + TrackHeight + 10;

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

    private double TrackWidth => Math.Max(1, ActualWidth - 2 * TrackPadding);

    private double TimeToX(double seconds)
    {
        var dur = Math.Max(Duration, 1);
        return TrackPadding + (seconds / dur) * TrackWidth;
    }

    private double XToTime(double x)
    {
        var dur = Math.Max(Duration, 1);
        var ratio = Math.Clamp((x - TrackPadding) / TrackWidth, 0, 1);
        return ratio * dur;
    }

    private void Redraw()
    {
        if (ActualWidth <= 0) return;

        MarkerCanvas.Width = ActualWidth;
        MarkerCanvas.Height = ActualHeight;

        TimelineFrame.Width = ActualWidth;
        TimelineFrame.Height = ActualHeight;
        Canvas.SetLeft(TimelineFrame, 0);

        TrackShell.Width = TrackWidth;
        Canvas.SetLeft(TrackShell, TrackPadding);

        // Track background
        TrackBg.Width = TrackWidth;
        Canvas.SetLeft(TrackBg, TrackPadding);

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
        Canvas.SetLeft(ProgressBar, TrackPadding);
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
                var maxRegionX = Math.Max(TrackPadding, ActualWidth - TrackPadding);
                var x1 = Math.Clamp(TimeToX(de.StartTimeS), TrackPadding, maxRegionX);
                var x2 = Math.Clamp(TimeToX(Math.Max(de.StartTimeS, de.EndTimeS)), TrackPadding, maxRegionX);
                var width = Math.Max(8, x2 - x1);
                var rect = new Border
                {
                    Width = width,
                    Height = TrackHeight + 4,
                    Background = new SolidColorBrush(ParseColor(NormalizeTimelineColor(de.Color), de.IsInferred ? (byte)90 : (byte)64)),
                    BorderBrush = de.IsInferred
                        ? new SolidColorBrush(ParseColor(NormalizeTimelineColor(de.Color), 190))
                        : null,
                    BorderThickness = de.IsInferred ? new Thickness(1) : new Thickness(0),
                    CornerRadius = new CornerRadius(2),
                };
                Canvas.SetLeft(rect, x1);
                Canvas.SetTop(rect, TrackTop - 2);
                MarkerCanvas.Children.Add(rect);

                if (de.IsInferred && width >= 34)
                {
                    var label = CreateMarkerLabel(de.ShortLabel, de.Color);
                    label.Opacity = 0.95;
                    Canvas.SetLeft(label, Math.Clamp(x1 + 3, 0, Math.Max(0, ActualWidth - 72)));
                    Canvas.SetTop(label, TrackTop + TrackHeight + 4);
                    MarkerCanvas.Children.Add(label);
                }

                _markerHitAreas.Add(new MarkerHitInfo
                {
                    X = x1 + width / 2,
                    Label = string.IsNullOrWhiteSpace(de.Tooltip)
                        ? $"{VodPlayerViewModel.FormatTime((int)de.StartTimeS)}-{VodPlayerViewModel.FormatTime((int)de.EndTimeS)} {de.Name}"
                        : de.Tooltip,
                });
            }
        }

        const double minBookmarkLabelGap = 22;
        var labelBuckets = new List<TimelineLabelBucket>();
        double lastBookmarkLabelX = double.NegativeInfinity;

        // Game event markers
        if (Events != null)
        {
            foreach (var evt in Events.OrderBy(static evt => evt.GameTimeS))
            {
                var maxMarkerX = Math.Max(TrackPadding, ActualWidth - TrackPadding);
                var x = Math.Clamp(TimeToX(evt.GameTimeS), TrackPadding, maxMarkerX);
                var shape = CreateMarkerShape(evt.Shape, evt.Color);
                Canvas.SetLeft(shape, Math.Clamp(x - MarkerSize / 2, 1, Math.Max(1, ActualWidth - MarkerSize - 1)));
                Canvas.SetTop(shape, EventMarkerTop);
                MarkerCanvas.Children.Add(shape);

                AddEventLabelBucket(labelBuckets, evt, x);

                _markerHitAreas.Add(new MarkerHitInfo
                {
                    X = x,
                    Label = evt.TooltipText,
                });
            }
        }

        DrawEventLabelBuckets(labelBuckets);

        // Bookmark markers (purple diamonds)
        if (Bookmarks != null)
        {
            foreach (var bm in Bookmarks)
            {
                var maxMarkerX = Math.Max(TrackPadding, ActualWidth - TrackPadding);
                var x = Math.Clamp(TimeToX(bm.GameTimeS), TrackPadding, maxMarkerX);
                var shape = CreateMarkerShape(MarkerShape.Diamond, bm.MarkerColorHex);
                Canvas.SetLeft(shape, Math.Clamp(x - MarkerSize / 2, 1, Math.Max(1, ActualWidth - MarkerSize - 1)));
                Canvas.SetTop(shape, BookmarkMarkerTop);
                MarkerCanvas.Children.Add(shape);

                if (x - lastBookmarkLabelX >= minBookmarkLabelGap)
                {
                    var labelText = bm.IsClip ? "CLIP" : "BM";
                    var label = CreateMarkerLabel(labelText, bm.MarkerColorHex);
                    Canvas.SetLeft(label, Math.Clamp(x - 12, 0, Math.Max(0, ActualWidth - 28)));
                    Canvas.SetTop(label, BookmarkMarkerTop + 14);
                    MarkerCanvas.Children.Add(label);
                    lastBookmarkLabelX = x;
                }

                var fullLabel = string.IsNullOrEmpty(bm.Note)
                    ? $"{bm.TimeText} Note"
                    : $"{bm.TimeText} {bm.Note}";
                if (bm.IsClip) fullLabel += " [CLIP]";

                _markerHitAreas.Add(new MarkerHitInfo { X = x, Label = fullLabel });
            }
        }
    }

    private static void AddEventLabelBucket(List<TimelineLabelBucket> buckets, TimelineEvent evt, double x)
    {
        const double bucketGap = 18;
        var bucket = buckets.LastOrDefault(item => x - item.EndX <= bucketGap);
        if (bucket is null)
        {
            bucket = new TimelineLabelBucket(x);
            buckets.Add(bucket);
        }

        bucket.EndX = x;
        bucket.Events.Add(evt);
    }

    private void DrawEventLabelBuckets(IReadOnlyList<TimelineLabelBucket> buckets)
    {
        foreach (var bucket in buckets)
        {
            var important = bucket.Events
                .OrderByDescending(static evt => evt.IsCombatEvent)
                .ThenBy(static evt => EventLabelPriority(evt.EventType))
                .Take(2)
                .Select(static evt => evt.ShortLabel)
                .Distinct()
                .ToArray();

            var text = important.Length == 0
                ? "EVT"
                : string.Join("+", important);

            if (bucket.Events.Count > important.Length)
            {
                text += $"+{bucket.Events.Count - important.Length}";
            }

            var color = bucket.Events.FirstOrDefault(static evt => evt.IsCombatEvent)?.Color
                ?? bucket.Events[0].Color;
            var label = CreateMarkerLabel(text, color);
            Canvas.SetLeft(label, Math.Clamp(bucket.CenterX - 14, 0, Math.Max(0, ActualWidth - 44)));
            Canvas.SetTop(label, EventMarkerTop - 16);
            MarkerCanvas.Children.Add(label);
        }
    }

    private static int EventLabelPriority(string eventType) => eventType.ToUpperInvariant() switch
    {
        "DEATH" => 0,
        "KILL" => 1,
        "ASSIST" => 2,
        "DRAGON" or "BARON" or "HERALD" => 3,
        "TURRET" or "INHIBITOR" => 4,
        _ => 5,
    };

    /// <summary>
    /// v2.16: small label rendered next to a timeline marker. Color-matched to
    /// the marker so the user can see "DEAD" in red, "DRG" in gold, etc.
    /// without relying on the colored bar alone.
    /// </summary>
    private static TextBlock CreateMarkerLabel(string text, string colorHex)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ParseColor(colorHex)),
            Opacity = 0.9,
        };
    }

    private static FrameworkElement CreateMarkerShape(MarkerShape shape, string colorHex)
    {
        var color = ParseColor(colorHex);
        var brush = new SolidColorBrush(color);

        // All markers are minimal vertical bars — clean, not childish
        var height = shape switch
        {
            ViewModels.MarkerShape.Star => 14.0,
            ViewModels.MarkerShape.Diamond => 12.0,
            _ => 10.0,
        };

        var width = shape switch
        {
            ViewModels.MarkerShape.Square => 4.0,
            _ => 2.0,
        };

        return new Border
        {
            Width = width,
            Height = height,
            Background = brush,
            CornerRadius = new CornerRadius(1),
            Opacity = 0.8,
        };

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
            Canvas.SetLeft(Tooltip, Math.Clamp(closest.X - 44, 0, Math.Max(0, ActualWidth - 180)));
            Tooltip.Visibility = Visibility.Visible;
        }
        else
        {
            // Show time tooltip when hovering the track area
            var time = XToTime(pos.X);
            if (time >= 0 && time <= Duration)
            {
                TooltipText.Text = VodPlayerViewModel.FormatTime((int)time);
                Canvas.SetLeft(Tooltip, Math.Clamp(pos.X - 28, 0, Math.Max(0, ActualWidth - 120)));
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
        return Windows.UI.Color.FromArgb(alpha, 138, 128, 168); // #8A80A8 neutral fallback
    }

    private static string NormalizeTimelineColor(string? colorHex)
    {
        var normalized = (colorHex ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return AppSemanticPalette.NeutralHex;
        }

        if (string.Equals(normalized, AppSemanticPalette.AccentBlueHex, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, AppSemanticPalette.AccentGoldHex, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, AppSemanticPalette.AccentTealHex, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, AppSemanticPalette.PositiveHex, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, AppSemanticPalette.NegativeHex, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, AppSemanticPalette.NeutralHex, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return normalized.ToLowerInvariant() switch
        {
            "#22c55e" or "#28c76f" => AppSemanticPalette.PositiveHex,
            "#ef4444" or "#ea5455" => AppSemanticPalette.NegativeHex,
            "#0099ff" or "#3b82f6" => AppSemanticPalette.AccentBlueHex,
            "#c89b3c" or "#fbbf24" => AppSemanticPalette.AccentGoldHex,
            "#06b6d4" or "#f97316" => AppSemanticPalette.AccentTealHex,
            "#8b5cf6" or "#6366f1" => AppSemanticPalette.NeutralHex,
            _ => AppSemanticPalette.NeutralHex,
        };
    }

    private class MarkerHitInfo
    {
        public double X { get; set; }
        public string Label { get; set; } = "";
    }

    private sealed class TimelineLabelBucket
    {
        public TimelineLabelBucket(double startX)
        {
            StartX = startX;
            EndX = startX;
        }

        public double StartX { get; }
        public double EndX { get; set; }
        public List<TimelineEvent> Events { get; } = [];
        public double CenterX => (StartX + EndX) / 2;
    }
}

#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.Helpers;

/// <summary>
/// Lightweight hover tooltip replacement for WinUI 3's <c>ToolTipService</c>,
/// which only lets you customize look — not the ~1000ms reveal delay. This
/// shows a HUD-themed popup after a short delay and hides on pointer exit.
///
/// <para>Usage in XAML:</para>
/// <code>
///   xmlns:hud="using:Revu.App.Helpers"
///   &lt;Button hud:HoverTip.Text="Dashboard" .../&gt;
/// </code>
///
/// <para>Appearance matches the Revu HUD theme (dark sidebar background,
/// purple accent border, Share Tech Mono). Delay is <see cref="DelayMs"/>
/// milliseconds; tweak the constant to taste.</para>
/// </summary>
public static class HoverTip
{
    private const int DelayMs = 150;
    private const double TipOffsetX = 12;

    public static readonly DependencyProperty TipTextProperty =
        DependencyProperty.RegisterAttached(
            "TipText",
            typeof(string),
            typeof(HoverTip),
            new PropertyMetadata(null, OnTextChanged));

    public static string? GetTipText(DependencyObject obj) => (string?)obj.GetValue(TipTextProperty);
    public static void SetTipText(DependencyObject obj, string? value) => obj.SetValue(TipTextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        // Detach prior handlers if the property is being reset.
        element.PointerEntered -= OnPointerEntered;
        element.PointerExited -= OnPointerExited;
        element.PointerCanceled -= OnPointerExited;
        element.Unloaded -= OnUnloaded;

        if (!string.IsNullOrWhiteSpace(e.NewValue as string))
        {
            element.PointerEntered += OnPointerEntered;
            element.PointerExited += OnPointerExited;
            element.PointerCanceled += OnPointerExited;
            element.Unloaded += OnUnloaded;
        }
    }

    // Per-element state held off to the side so we don't bloat FrameworkElement.
    private sealed class State
    {
        public Popup? Popup;
        public DispatcherTimer? Timer;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "_State", typeof(State), typeof(HoverTip), new PropertyMetadata(null));

    private static State GetOrCreateState(FrameworkElement element)
    {
        var s = (State?)element.GetValue(StateProperty);
        if (s is null)
        {
            s = new State();
            element.SetValue(StateProperty, s);
        }
        return s;
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var text = GetTipText(element);
        if (string.IsNullOrWhiteSpace(text)) return;

        var state = GetOrCreateState(element);
        state.Timer?.Stop();
        state.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DelayMs) };
        state.Timer.Tick += (_, _) =>
        {
            state.Timer?.Stop();
            ShowTip(element, text!, state);
        };
        state.Timer.Start();
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        Hide(element);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        Hide(element);
    }

    private static void Hide(FrameworkElement element)
    {
        var state = (State?)element.GetValue(StateProperty);
        if (state is null) return;
        state.Timer?.Stop();
        if (state.Popup is { } p)
        {
            p.IsOpen = false;
        }
    }

    private static void ShowTip(FrameworkElement anchor, string text, State state)
    {
        // Build a dark HUD-style popup body. We construct it fresh each time so
        // the style picks up any theme changes.
        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["ToolTipBackground"],
            BorderBrush = (Brush)Application.Current.Resources["ToolTipBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.Resources["ToolTipForeground"],
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                FontSize = 12,
                CharacterSpacing = 80,
            },
        };

        var popup = state.Popup ?? new Popup();
        popup.Child = border;
        popup.XamlRoot = anchor.XamlRoot;

        // Position to the right of the anchor, vertically centered.
        var transform = anchor.TransformToVisual(null);
        var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        border.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        popup.HorizontalOffset = origin.X + anchor.ActualWidth + TipOffsetX;
        popup.VerticalOffset = origin.Y + (anchor.ActualHeight - border.DesiredSize.Height) / 2.0;

        state.Popup = popup;
        popup.IsOpen = true;
    }
}

#nullable enable

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.Controls;

/// <summary>
/// 5-button horizontal mood selector.
/// Buttons: Tilted (red), Off (orange), Neutral (gray), Good (green), Locked In (emerald).
/// Selected button is highlighted with the mood color background.
/// </summary>
public sealed partial class MoodSelector : UserControl
{
    // Mood colors: 1=Tilted(red), 2=Off(orange), 3=Neutral(gray), 4=Good(green), 5=LockedIn(emerald)
    private static readonly Dictionary<int, Windows.UI.Color> MoodColors = new()
    {
        { 1, ColorHelper.FromArgb(255, 239, 68, 68) },   // #ef4444
        { 2, ColorHelper.FromArgb(255, 249, 115, 22) },  // #f97316
        { 3, ColorHelper.FromArgb(255, 107, 114, 128) },  // #6b7280
        { 4, ColorHelper.FromArgb(255, 34, 197, 94) },    // #22c55e
        { 5, ColorHelper.FromArgb(255, 16, 185, 129) },   // #10b981
    };

    private static readonly SolidColorBrush DefaultBackground = new(ColorHelper.FromArgb(255, 30, 30, 46));
    private static readonly SolidColorBrush DefaultForeground = new(ColorHelper.FromArgb(255, 112, 112, 160));
    private static readonly SolidColorBrush SelectedForeground = new(Colors.White);

    public MoodSelector()
    {
        InitializeComponent();
    }

    // ── SelectedMood dependency property ────────────────────────────

    public static readonly DependencyProperty SelectedMoodProperty =
        DependencyProperty.Register(
            nameof(SelectedMood), typeof(int), typeof(MoodSelector),
            new PropertyMetadata(0, OnSelectedMoodChanged));

    public int SelectedMood
    {
        get => (int)GetValue(SelectedMoodProperty);
        set => SetValue(SelectedMoodProperty, value);
    }

    private static void OnSelectedMoodChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MoodSelector selector)
        {
            selector.UpdateButtonStates();
        }
    }

    // ── Button click handler ────────────────────────────────────────

    private void OnMoodClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out var mood))
        {
            SelectedMood = mood;
        }
    }

    // ── Visual state update ─────────────────────────────────────────

    private void UpdateButtonStates()
    {
        var buttons = new Dictionary<int, Button>
        {
            { 1, TiltedBtn },
            { 2, OffBtn },
            { 3, NeutralBtn },
            { 4, GoodBtn },
            { 5, LockedInBtn }
        };

        foreach (var (mood, btn) in buttons)
        {
            if (mood == SelectedMood && MoodColors.TryGetValue(mood, out var color))
            {
                btn.Background = new SolidColorBrush(color);
                btn.Foreground = SelectedForeground;
                btn.BorderBrush = new SolidColorBrush(color);
            }
            else
            {
                btn.Background = DefaultBackground;
                btn.Foreground = DefaultForeground;
                btn.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 30, 30, 46));
            }
        }
    }
}

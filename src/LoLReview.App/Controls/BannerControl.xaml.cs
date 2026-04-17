#nullable enable

using LoLReview.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.Controls;

public enum BannerTone
{
    Neutral,
    Positive,
    Negative,
    Warning,
    Accent,
}

/// <summary>
/// Colored-left-bar banner. Pulses the bar using composition opacity,
/// and color-codes left bar + glyph based on Tone.
/// </summary>
public sealed partial class BannerControl : UserControl
{
    public BannerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(
            nameof(Tone),
            typeof(BannerTone),
            typeof(BannerControl),
            new PropertyMetadata(BannerTone.Neutral, (d, e) => ((BannerControl)d).ApplyTone()));

    public BannerTone Tone
    {
        get => (BannerTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(
            nameof(Message),
            typeof(string),
            typeof(BannerControl),
            new PropertyMetadata("", (d, e) => ((BannerControl)d).MessageTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(BannerControl),
            new PropertyMetadata("\uE946", (d, e) => ((BannerControl)d).IconGlyph.Glyph = e.NewValue?.ToString() ?? "\uE946"));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTone();
        AnimationHelper.AttachPulseOpacity(ToneBar, 0.55, 1.0, 2.4);
    }

    private void ApplyTone()
    {
        if (ToneBar is null || IconGlyph is null) return;

        Brush brush = Tone switch
        {
            BannerTone.Positive => (Brush)Application.Current.Resources["WinGreenBrush"],
            BannerTone.Negative => (Brush)Application.Current.Resources["LossRedBrush"],
            BannerTone.Warning => (Brush)Application.Current.Resources["AccentGoldBrush"],
            BannerTone.Accent => (Brush)Application.Current.Resources["AccentBlueBrush"],
            _ => (Brush)Application.Current.Resources["NeutralAccentBrush"],
        };

        ToneBar.Fill = brush;
        IconGlyph.Foreground = brush;
    }
}

#nullable enable

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Revu.App.Controls;

/// <summary>
/// Page-level hero zone with gradient wash backdrop, blinking cursor underscore,
/// and DisplayFont title. Matches the .hero block in mockups/app-mockup.html.
/// </summary>
public sealed partial class HeroHeader : UserControl
{
    public HeroHeader()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty EyebrowTextProperty =
        DependencyProperty.Register(
            nameof(EyebrowText),
            typeof(string),
            typeof(HeroHeader),
            new PropertyMetadata("", (d, e) => ((HeroHeader)d).EyebrowTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string EyebrowText
    {
        get => (string)GetValue(EyebrowTextProperty);
        set => SetValue(EyebrowTextProperty, value);
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(HeroHeader),
            new PropertyMetadata("", (d, e) => ((HeroHeader)d).TitleTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(
            nameof(Subtitle),
            typeof(string),
            typeof(HeroHeader),
            new PropertyMetadata("", OnSubtitleChanged));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (HeroHeader)d;
        var text = e.NewValue?.ToString() ?? "";
        self.SubtitleTextBlock.Text = text;
        self.SubtitleTextBlock.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartCursorBlink();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopCursorBlink();
    }

    private void StartCursorBlink()
    {
        StopCursorBlink();

        try
        {
            // Use a Composition opacity animation instead of a DispatcherTimer so
            // the blink runs on the compositor thread with zero UI-thread wakes.
            // 0→1→0 over 1 s (each half = 500 ms) replicates the former 2 Hz toggle.
            var visual = ElementCompositionPreview.GetElementVisual(CursorTextBlock);
            var compositor = visual.Compositor;

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0.0f, 1.0f);
            anim.InsertKeyFrame(0.5f, 0.0f);
            anim.InsertKeyFrame(1.0f, 1.0f);
            anim.Duration = TimeSpan.FromSeconds(1.0);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation(nameof(visual.Opacity), anim);
        }
        catch { }
    }

    private void StopCursorBlink()
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(CursorTextBlock);
            visual.StopAnimation(nameof(visual.Opacity));
        }
        catch { }
    }
}

#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Controls;

/// <summary>
/// Page-level hero zone with gradient wash backdrop, blinking cursor underscore,
/// and DisplayFont title. Matches the .hero block in mockups/app-mockup.html.
/// </summary>
public sealed partial class HeroHeader : UserControl
{
    private DispatcherTimer? _cursorTimer;

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
        _cursorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _cursorTimer.Tick += (_, _) =>
        {
            CursorTextBlock.Opacity = CursorTextBlock.Opacity > 0.5 ? 0.0 : 1.0;
        };
        _cursorTimer.Start();
    }

    private void StopCursorBlink()
    {
        if (_cursorTimer is not null)
        {
            _cursorTimer.Stop();
            _cursorTimer = null;
        }
    }
}

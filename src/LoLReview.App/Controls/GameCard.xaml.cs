#nullable enable

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.Controls;

/// <summary>
/// Reusable game row card with W/L bar, champion, KDA, stats, and review button.
/// Color-coded W/L bar on left edge.
/// </summary>
public sealed partial class GameCard : UserControl
{
    private static readonly SolidColorBrush WinBrush = new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush LossBrush = new(ColorHelper.FromArgb(255, 239, 68, 68));
    private static readonly SolidColorBrush HoverBg = new(ColorHelper.FromArgb(255, 22, 22, 31));
    private static readonly SolidColorBrush NormalBg = new(ColorHelper.FromArgb(255, 18, 18, 26));

    public GameCard()
    {
        InitializeComponent();
    }

    // ── Events ──────────────────────────────────────────────────────

    public event EventHandler<long>? ReviewRequested;
    public event EventHandler<long>? HideRequested;

    // ── Dependency Properties ───────────────────────────────────────

    public static readonly DependencyProperty GameIdProperty =
        DependencyProperty.Register(nameof(GameId), typeof(long), typeof(GameCard),
            new PropertyMetadata(0L));

    public long GameId
    {
        get => (long)GetValue(GameIdProperty);
        set => SetValue(GameIdProperty, value);
    }

    public static readonly DependencyProperty ChampionProperty =
        DependencyProperty.Register(nameof(Champion), typeof(string), typeof(GameCard),
            new PropertyMetadata("", (d, e) => ((GameCard)d).ChampionText.Text = e.NewValue?.ToString() ?? ""));

    public string Champion
    {
        get => (string)GetValue(ChampionProperty);
        set => SetValue(ChampionProperty, value);
    }

    public static readonly DependencyProperty WinProperty =
        DependencyProperty.Register(nameof(Win), typeof(bool), typeof(GameCard),
            new PropertyMetadata(false, OnWinChanged));

    public bool Win
    {
        get => (bool)GetValue(WinProperty);
        set => SetValue(WinProperty, value);
    }

    private static void OnWinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GameCard card)
        {
            var win = (bool)e.NewValue;
            card.WinLossBar.Background = win ? WinBrush : LossBrush;
            card.WinLossText.Text = win ? "W" : "L";
            card.WinLossText.Foreground = win ? WinBrush : LossBrush;
        }
    }

    public static readonly DependencyProperty KillsProperty =
        DependencyProperty.Register(nameof(Kills), typeof(int), typeof(GameCard),
            new PropertyMetadata(0, OnKdaChanged));

    public int Kills
    {
        get => (int)GetValue(KillsProperty);
        set => SetValue(KillsProperty, value);
    }

    public static readonly DependencyProperty DeathsProperty =
        DependencyProperty.Register(nameof(Deaths), typeof(int), typeof(GameCard),
            new PropertyMetadata(0, OnKdaChanged));

    public int Deaths
    {
        get => (int)GetValue(DeathsProperty);
        set => SetValue(DeathsProperty, value);
    }

    public static readonly DependencyProperty AssistsProperty =
        DependencyProperty.Register(nameof(Assists), typeof(int), typeof(GameCard),
            new PropertyMetadata(0, OnKdaChanged));

    public int Assists
    {
        get => (int)GetValue(AssistsProperty);
        set => SetValue(AssistsProperty, value);
    }

    private static void OnKdaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GameCard card)
        {
            card.KdaText.Text = $"{card.Kills}/{card.Deaths}/{card.Assists}";
        }
    }

    public static readonly DependencyProperty KdaRatioProperty =
        DependencyProperty.Register(nameof(KdaRatio), typeof(double), typeof(GameCard),
            new PropertyMetadata(0.0, (d, e) =>
            {
                if (d is GameCard card)
                {
                    var ratio = (double)e.NewValue;
                    card.KdaRatioText.Text = $"({ratio:F1})";

                    // Color code KDA
                    if (ratio >= 5.0)
                        card.KdaRatioText.Foreground = WinBrush;
                    else if (ratio >= 3.0)
                        card.KdaRatioText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 200, 155, 60));
                    else
                        card.KdaRatioText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 112, 112, 160));
                }
            }));

    public double KdaRatio
    {
        get => (double)GetValue(KdaRatioProperty);
        set => SetValue(KdaRatioProperty, value);
    }

    public static readonly DependencyProperty CsPerMinProperty =
        DependencyProperty.Register(nameof(CsPerMin), typeof(double), typeof(GameCard),
            new PropertyMetadata(0.0, (d, e) =>
            {
                if (d is GameCard card)
                    card.CsText.Text = $"CS {(double)e.NewValue:F1}/m";
            }));

    public double CsPerMin
    {
        get => (double)GetValue(CsPerMinProperty);
        set => SetValue(CsPerMinProperty, value);
    }

    public static readonly DependencyProperty VisionScoreProperty =
        DependencyProperty.Register(nameof(VisionScore), typeof(int), typeof(GameCard),
            new PropertyMetadata(0, (d, e) =>
            {
                if (d is GameCard card)
                    card.VisionText.Text = $"Vis {(int)e.NewValue}";
            }));

    public int VisionScore
    {
        get => (int)GetValue(VisionScoreProperty);
        set => SetValue(VisionScoreProperty, value);
    }

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(string), typeof(GameCard),
            new PropertyMetadata("", (d, e) => ((GameCard)d).DurationText.Text = e.NewValue?.ToString() ?? ""));

    public string Duration
    {
        get => (string)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public static readonly DependencyProperty GameModeProperty =
        DependencyProperty.Register(nameof(GameMode), typeof(string), typeof(GameCard),
            new PropertyMetadata("", (d, e) => ((GameCard)d).GameModeText.Text = e.NewValue?.ToString() ?? ""));

    public string GameMode
    {
        get => (string)GetValue(GameModeProperty);
        set => SetValue(GameModeProperty, value);
    }

    public static readonly DependencyProperty DatePlayedProperty =
        DependencyProperty.Register(nameof(DatePlayed), typeof(string), typeof(GameCard),
            new PropertyMetadata("", (d, e) => ((GameCard)d).DateText.Text = e.NewValue?.ToString() ?? ""));

    public string DatePlayed
    {
        get => (string)GetValue(DatePlayedProperty);
        set => SetValue(DatePlayedProperty, value);
    }

    public static readonly DependencyProperty HasReviewProperty =
        DependencyProperty.Register(nameof(HasReview), typeof(bool), typeof(GameCard),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is GameCard card)
                {
                    var reviewed = (bool)e.NewValue;
                    card.ReviewButton.Content = reviewed ? "Edit" : "Review";
                }
            }));

    public bool HasReview
    {
        get => (bool)GetValue(HasReviewProperty);
        set => SetValue(HasReviewProperty, value);
    }

    public static readonly DependencyProperty TagsProperty =
        DependencyProperty.Register(nameof(Tags), typeof(string), typeof(GameCard),
            new PropertyMetadata(""));

    public string Tags
    {
        get => (string)GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    public static readonly DependencyProperty DamageProperty =
        DependencyProperty.Register(nameof(Damage), typeof(string), typeof(GameCard),
            new PropertyMetadata("", (d, e) =>
            {
                if (d is GameCard card)
                    card.DamageText.Text = $"{e.NewValue} dmg";
            }));

    public string Damage
    {
        get => (string)GetValue(DamageProperty);
        set => SetValue(DamageProperty, value);
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        ReviewRequested?.Invoke(this, GameId);
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        HideRequested?.Invoke(this, GameId);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        CardBorder.Background = HoverBg;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        CardBorder.Background = NormalBg;
    }
}

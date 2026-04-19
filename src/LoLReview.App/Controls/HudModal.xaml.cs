#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LoLReview.App.Controls;

/// <summary>
/// HUD-themed modal overlay, lives inside the app's visual tree.
///
/// Replaces <see cref="ContentDialog"/> for Coach-related popups so
/// they match the dark violet theme instead of the Windows default
/// system dialog. Pages add one of these to their root grid, bind
/// <see cref="IsOpen"/>, and set <see cref="Body"/> / <see cref="Footer"/>
/// to whatever content they want inside the shell.
///
/// Backdrop click or Close button dismisses; Escape key dismisses when
/// the modal has focus. Content inside the card is shielded from the
/// backdrop-tap so clicks inside the card don't bubble up and close it.
/// </summary>
public sealed partial class HudModal : UserControl
{
    public HudModal()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    // ── Eyebrow (tiny monospace breadcrumb above the title) ──

    public static readonly DependencyProperty EyebrowProperty =
        DependencyProperty.Register(
            nameof(Eyebrow),
            typeof(string),
            typeof(HudModal),
            new PropertyMetadata("MODAL", OnEyebrowChanged));

    public string Eyebrow
    {
        get => (string)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    private static void OnEyebrowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HudModal modal)
        {
            modal.EyebrowText.Text = (e.NewValue as string ?? "").ToUpperInvariant();
        }
    }

    // ── Title (string) ──

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(HudModal),
            new PropertyMetadata("", OnTitleChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HudModal modal)
        {
            modal.TitleText.Text = e.NewValue as string ?? "";
        }
    }

    // ── Body (arbitrary UIElement) ──

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(
            nameof(Body),
            typeof(object),
            typeof(HudModal),
            new PropertyMetadata(null, OnBodyChanged));

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    private static void OnBodyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HudModal modal)
        {
            modal.BodyPresenter.Content = e.NewValue;
        }
    }

    // ── Footer (optional, e.g. a StackPanel of action buttons) ──

    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(
            nameof(Footer),
            typeof(object),
            typeof(HudModal),
            new PropertyMetadata(null, OnFooterChanged));

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    private static void OnFooterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HudModal modal)
        {
            modal.FooterPresenter.Content = e.NewValue;
            modal.FooterBorder.Visibility = e.NewValue is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    // ── IsOpen (controls Visibility + focus) ──

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(
            nameof(IsOpen),
            typeof(bool),
            typeof(HudModal),
            new PropertyMetadata(false, OnIsOpenChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HudModal modal)
        {
            modal.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            if ((bool)e.NewValue)
            {
                // Let Escape work without requiring the user to click first.
                modal.Focus(FocusState.Programmatic);
            }
        }
    }

    /// <summary>Fires when the modal requests to close (backdrop click,
    /// close button, or Escape). The host decides whether to honor it
    /// by setting <see cref="IsOpen"/> to false.</summary>
    public event System.EventHandler? CloseRequested;

    private void RaiseCloseRequested()
    {
        CloseRequested?.Invoke(this, System.EventArgs.Empty);
    }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        // Only the outer Grid registers here — taps on the card are
        // caught by OnCardTapped and marked handled, so this only fires
        // when the user clicks the dim backdrop.
        RaiseCloseRequested();
    }

    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        // Swallow so the backdrop doesn't treat a card-interior click
        // as a dismiss.
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        RaiseCloseRequested();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            RaiseCloseRequested();
        }
    }
}

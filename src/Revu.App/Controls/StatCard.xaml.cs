#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.Controls;

/// <summary>
/// Reusable stat display card: dim label on top, bold value below.
/// Dark card background (#12121a) with rounded corners.
/// </summary>
public sealed partial class StatCard : UserControl
{
    public StatCard()
    {
        InitializeComponent();
    }

    // ── Label ────────────────────────────────────────────────────────

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(StatCard),
            new PropertyMetadata("", OnLabelChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatCard card)
        {
            card.LabelText.Text = e.NewValue?.ToString() ?? "";
        }
    }

    // ── Value ────────────────────────────────────────────────────────

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(string), typeof(StatCard),
            new PropertyMetadata("", OnValueChanged));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatCard card)
        {
            card.ValueText.Text = e.NewValue?.ToString() ?? "";
        }
    }

    // ── ValueColor ──────────────────────────────────────────────────

    public static readonly DependencyProperty ValueColorProperty =
        DependencyProperty.Register(
            nameof(ValueColor), typeof(Brush), typeof(StatCard),
            new PropertyMetadata(null, OnValueColorChanged));

    public Brush? ValueColor
    {
        get => (Brush?)GetValue(ValueColorProperty);
        set => SetValue(ValueColorProperty, value);
    }

    private static void OnValueColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatCard card && e.NewValue is Brush brush)
        {
            card.ValueText.Foreground = brush;
        }
    }
}

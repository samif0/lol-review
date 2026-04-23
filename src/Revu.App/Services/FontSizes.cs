#nullable enable

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Revu.App.Services;

/// <summary>
/// App-wide font-size scale. XAML binds `FontSize` to one of the named
/// buckets below; when <see cref="Scale"/> changes, every derived
/// property raises PropertyChanged and every bound FontSize updates.
///
/// Eleven buckets mapped from the existing literal distribution:
///   7–8   → Micro
///   9     → Caption
///   10–11 → Meta
///   12    → Body
///   13    → BodyEmphasis
///   14–15 → Subtitle
///   16–18 → Title
///   20–24 → TitleLarge
///   26–28 → Hero
///   32–36 → HeroLarge
///   48–52 → Display
///
/// Implementation note: x:Bind's change-detection walks the binding path
/// looking for PropertyChanged on each step. Expression-bodied
/// properties like `public double Body => BodyBase * Scale` DO fire
/// PropertyChanged via [NotifyPropertyChangedFor] when backed by
/// CommunityToolkit.Mvvm source gen, but the generator's analyzer
/// doesn't always flag them as observable for x:Bind's compile-time
/// check — resulting in WMC1506. We use explicit hand-written
/// PropertyChanged invocations instead, which the XAML compiler
/// reliably picks up.
/// </summary>
public sealed class FontSizes : INotifyPropertyChanged
{
    public static FontSizes Instance { get; } = new();

    private FontSizes() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private const double MicroBase = 8;
    private const double CaptionBase = 9;
    private const double MetaBase = 11;
    private const double BodyBase = 12;
    private const double BodyEmphasisBase = 13;
    private const double SubtitleBase = 14;
    private const double TitleBase = 16;
    private const double TitleLargeBase = 20;
    private const double HeroBase = 26;
    private const double HeroLargeBase = 32;
    private const double DisplayBase = 48;

    public const double ScaleMin = 0.7;
    public const double ScaleMax = 2.0;
    public const double ScaleStep = 0.05;

    private double _scale = 1.0;
    public double Scale
    {
        get => _scale;
        set
        {
            var clamped = System.Math.Clamp(value, ScaleMin, ScaleMax);
            if (System.Math.Abs(clamped - _scale) < 0.0001) return;
            _scale = clamped;
            Notify();
            Notify(nameof(Micro));
            Notify(nameof(Caption));
            Notify(nameof(Meta));
            Notify(nameof(Body));
            Notify(nameof(BodyEmphasis));
            Notify(nameof(Subtitle));
            Notify(nameof(Title));
            Notify(nameof(TitleLarge));
            Notify(nameof(Hero));
            Notify(nameof(HeroLarge));
            Notify(nameof(Display));
        }
    }

    public double Micro => MicroBase * _scale;
    public double Caption => CaptionBase * _scale;
    public double Meta => MetaBase * _scale;
    public double Body => BodyBase * _scale;
    public double BodyEmphasis => BodyEmphasisBase * _scale;
    public double Subtitle => SubtitleBase * _scale;
    public double Title => TitleBase * _scale;
    public double TitleLarge => TitleLargeBase * _scale;
    public double Hero => HeroBase * _scale;
    public double HeroLarge => HeroLargeBase * _scale;
    public double Display => DisplayBase * _scale;

    public void SetScale(double value) => Scale = value;
}

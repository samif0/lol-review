#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.Styling;

/// <summary>
/// Shared semantic palette for view-model-driven brushes.
/// Keep these values aligned with Themes/AppTheme.xaml.
/// </summary>
public static class AppSemanticPalette
{
    public const string PrimaryTextHex = "#EDF3F0";
    public const string SecondaryTextHex = "#A0B1AB";
    public const string MutedTextHex = "#66756F";

    public const string NeutralHex = "#93A59E";
    public const string NeutralDimHex = "#111918";
    public const string SubtleBorderHex = "#24312E";
    public const string TagSurfaceHex = "#101817";

    public const string AccentBlueHex = "#89F3C7";
    public const string AccentBlueDimHex = "#113229";
    public const string AccentGoldHex = "#D7A36A";
    public const string AccentGoldDimHex = "#312114";
    public const string AccentTealHex = "#A6DF78";
    public const string AccentTealDimHex = "#20301A";

    public const string PositiveHex = "#78D6AE";
    public const string PositiveDimHex = "#11251E";
    public const string NegativeHex = "#D38C90";
    public const string NegativeDimHex = "#332025";

    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush Brush(string hex)
    {
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            var normalized = hex.TrimStart('#');
            var r = byte.Parse(normalized[..2], System.Globalization.NumberStyles.HexNumber);
            var g = byte.Parse(normalized[2..4], System.Globalization.NumberStyles.HexNumber);
            var b = byte.Parse(normalized[4..6], System.Globalization.NumberStyles.HexNumber);
            brush = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
            BrushCache[hex] = brush;
        }

        return brush;
    }

    public static string WinRateHex(double value, double positiveThreshold = 55, double negativeThreshold = 45)
    {
        if (value >= positiveThreshold)
        {
            return PositiveHex;
        }

        if (value < negativeThreshold)
        {
            return NegativeHex;
        }

        return NeutralHex;
    }

    public static string OutcomeHex(bool positive) => positive ? PositiveHex : NegativeHex;

    public static string PracticedHex(bool practiced) => practiced ? PositiveHex : NeutralHex;

    public static string MentalRatingHex(int rating) => rating switch
    {
        >= 8 => PositiveHex,
        >= 5 => AccentBlueHex,
        >= 4 => AccentGoldHex,
        _ => NegativeHex
    };

    public static SolidColorBrush ObjectiveLevelBrush(int levelIndex) => Brush(ObjectiveLevelHex(levelIndex));

    public static string ObjectiveLevelHex(int levelIndex) => levelIndex switch
    {
        0 => NeutralHex,
        1 => AccentBlueHex,
        2 => AccentTealHex,
        3 => AccentGoldHex,
        _ => NeutralHex,
    };

    public static SolidColorBrush TagAccentBrush(string? polarity, string? sourceHex = null) =>
        Brush(TagAccentHex(polarity, sourceHex));

    public static SolidColorBrush TagSurfaceBrush(string? polarity, string? sourceHex = null) =>
        Brush(TagSurfaceHexFor(polarity, sourceHex));

    public static string TagAccentHex(string? polarity, string? sourceHex = null)
    {
        if (string.Equals(polarity, "positive", StringComparison.OrdinalIgnoreCase))
        {
            return PositiveHex;
        }

        if (string.Equals(polarity, "negative", StringComparison.OrdinalIgnoreCase))
        {
            return NegativeHex;
        }

        return NormalizeLegacyAccentHex(sourceHex);
    }

    public static string TagSurfaceHexFor(string? polarity, string? sourceHex = null)
    {
        var accentHex = TagAccentHex(polarity, sourceHex);
        if (HexEquals(accentHex, PositiveHex))
        {
            return PositiveDimHex;
        }

        if (HexEquals(accentHex, NegativeHex))
        {
            return NegativeDimHex;
        }

        if (HexEquals(accentHex, AccentGoldHex))
        {
            return AccentGoldDimHex;
        }

        if (HexEquals(accentHex, AccentTealHex))
        {
            return AccentTealDimHex;
        }

        return AccentBlueDimHex;
    }

    private static string NormalizeLegacyAccentHex(string? sourceHex)
    {
        var normalized = (sourceHex ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "#22c55e" or "#3bc98d" or "#1a3a1a" or "#17382d" => PositiveHex,
            "#ef4444" or "#ea7a73" or "#3f1111" or "#48262b" => NegativeHex,
            "#c89b3c" or "#c9a86a" => AccentGoldHex,
            "#8b5cf6" or "#8a7af2" => NeutralHex,
            "#0099ff" or "#3b82f6" or "#1e40af" => AccentBlueHex,
            _ => AccentBlueHex,
        };
    }

    private static bool HexEquals(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}

#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.Styling;

/// <summary>
/// Shared semantic palette for view-model-driven brushes.
/// Keep these values aligned with Themes/AppTheme.xaml.
/// Violet + Bronze futuristic HUD theme.
/// </summary>
public static class AppSemanticPalette
{
    public const string PrimaryTextHex = "#F0EEF8";
    public const string SecondaryTextHex = "#7A6E96";
    public const string MutedTextHex = "#4A3E60";

    public const string NeutralHex = "#8A80A8";
    public const string NeutralDimHex = "#13111E";
    public const string SubtleBorderHex = "#24203A";
    public const string TagSurfaceHex = "#110F1A";

    public const string AccentBlueHex = "#A78BFA";
    public const string AccentBlueDimHex = "#1A1430";
    public const string AccentGoldHex = "#C9956A";
    public const string AccentGoldDimHex = "#261C12";
    public const string AccentTealHex = "#8A7AF2";
    public const string AccentTealDimHex = "#181430";

    public const string PositiveHex = "#7EC9A0";
    public const string PositiveDimHex = "#0F1E18";
    public const string NegativeHex = "#D38C90";
    public const string NegativeDimHex = "#2A1820";

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

    public const string ObjectivePurpleHex = "#A78BFA";
    public const string ObjectiveOrangeHex = "#C9956A";

    public static string ObjectiveLevelHex(int levelIndex) => levelIndex switch
    {
        0 => "#7B8494",         // Exploring: Slate — just starting out
        1 => "#5EC4D4",         // Drilling: Cyan — gaining momentum
        2 => "#D4A44E",         // Ingraining: Amber — heating up
        3 => "#E8C15E",         // Ready: Bright gold — mastered
        _ => NeutralHex,
    };

    public static string ObjectiveLevelDimHex(int levelIndex) => levelIndex switch
    {
        0 => "#10121A",         // Exploring dim
        1 => "#0E1A1E",         // Drilling dim
        2 => "#1E1810",         // Ingraining dim
        3 => "#221C0E",         // Ready dim
        _ => NeutralDimHex,
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
            "#22c55e" or "#3bc98d" or "#1a3a1a" or "#17382d" or "#78d6ae" => PositiveHex,
            "#ef4444" or "#ea7a73" or "#3f1111" or "#48262b" or "#d38c90" => NegativeHex,
            "#c89b3c" or "#c9a86a" or "#d7a36a" or "#c9956a" => AccentGoldHex,
            "#8b5cf6" or "#8a7af2" or "#a78bfa" or "#7c3aed" => AccentBlueHex,
            "#0099ff" or "#3b82f6" or "#1e40af" or "#89f3c7" => AccentBlueHex,
            _ => AccentBlueHex,
        };
    }

    private static bool HexEquals(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}

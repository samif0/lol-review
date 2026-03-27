#nullable enable

namespace LoLReview.Core.Constants;

/// <summary>
/// Color palette — dark theme inspired by League client.
/// Ported from Python COLORS dict.
/// </summary>
public static class ColorPalette
{
    // Backgrounds
    /// <summary>Main content background.</summary>
    public const string BgDark = "#0a0a0f";

    /// <summary>Sidebar background.</summary>
    public const string BgSidebar = "#0d0d15";

    /// <summary>Card background.</summary>
    public const string BgCard = "#12121a";

    /// <summary>Card hover background.</summary>
    public const string BgCardHover = "#16161f";

    /// <summary>Input background.</summary>
    public const string BgInput = "#1a1a24";

    // Borders
    /// <summary>Subtle border.</summary>
    public const string Border = "#1e1e2e";

    /// <summary>Visible border.</summary>
    public const string BorderBright = "#2a2a3a";

    // Text
    /// <summary>Primary text.</summary>
    public const string Text = "#e8e8f0";

    /// <summary>Secondary text.</summary>
    public const string TextDim = "#7070a0";

    /// <summary>Muted text.</summary>
    public const string TextMuted = "#404060";

    // Accents
    /// <summary>Primary accent (blue).</summary>
    public const string AccentBlue = "#0099ff";

    /// <summary>Dim blue accent.</summary>
    public const string AccentBlueDim = "#004c80";

    /// <summary>Gold accent.</summary>
    public const string AccentGold = "#c89b3c";

    /// <summary>Purple accent.</summary>
    public const string AccentPurple = "#7c3aed";

    // Status
    /// <summary>Win color (green).</summary>
    public const string WinGreen = "#22c55e";

    /// <summary>Dim win color (green).</summary>
    public const string WinGreenDim = "#14532d";

    /// <summary>Loss color (red).</summary>
    public const string LossRed = "#ef4444";

    /// <summary>Dim loss color (red).</summary>
    public const string LossRedDim = "#7f1d1d";

    // Misc
    /// <summary>Tag background.</summary>
    public const string TagBg = "#1e1e2e";

    /// <summary>Star rating (active).</summary>
    public const string StarActive = "#fbbf24";

    /// <summary>Inactive star.</summary>
    public const string StarInactive = "#2a2a3a";

    // Sidebar
    /// <summary>Active nav item accent.</summary>
    public const string SidebarActive = "#0099ff";

    /// <summary>Hovered nav item background.</summary>
    public const string SidebarHover = "#14141e";
}

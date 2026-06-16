#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Guards for the config-save read-modify-write path (P-023 / P-020). The Settings
/// save sends every editable field; the contract is "null = leave unchanged". For the
/// FOLDER paths (Ascent / Clips / Backup) an empty string must ALSO mean "leave
/// unchanged" rather than "blank the saved path" — otherwise a save issued before the
/// Settings page finished rendering (or any caller that sends "") zeroes the stored
/// folders, which is exactly how ascent_folder/clips_folder/backup_folder got wiped
/// while the rest of config survived. A DELIBERATE clear (the Clear button) sends
/// <see cref="FolderClearSentinel"/>, which resolves to an explicit empty string.
/// Pure + side-effect-free so it is unit-testable from Revu.Core.Tests.
/// </summary>
public static class ConfigSaveGuards
{
    /// <summary>
    /// Sentinel the Settings UI sends to mean "the user explicitly cleared this folder."
    /// Space-padded and compared BEFORE trimming, so a real (trimmed) path can never
    /// collide with it. Must match settings.js FOLDER_CLEAR_SENTINEL byte-for-byte.
    /// </summary>
    public const string FolderClearSentinel = " __REVU_CLEAR__ ";

    /// <summary>
    /// Decide how a folder-path field from the config-save body should be written.
    /// Returns false (leave the saved value unchanged) for null OR empty/whitespace.
    /// Returns true with "" for the explicit-clear sentinel. Returns true with the
    /// trimmed path for any real value.
    /// </summary>
    public static bool TryResolveFolderWrite(string? value, out string resolved)
    {
        resolved = "";
        if (value is null) return false;
        if (value == FolderClearSentinel) return true; // explicit clear → set ""
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;         // blank → leave unchanged
        resolved = trimmed;
        return true;
    }

    /// <summary>
    /// Decide how a Riot-identity field (riot_id / riot_region) from the config-save
    /// body should be written. Same empty-overwrite hazard as the folders: a save
    /// issued before the Settings page rendered sends "" and would blank the linked
    /// account, breaking match-history sync (RiotProxyEnabled). Unlike folders there
    /// is no "clear" affordance, so empty/whitespace ALWAYS means "leave unchanged" —
    /// there is no sentinel and no explicit-clear path. Returns false (leave the saved
    /// value unchanged) for null OR empty/whitespace; returns true with the trimmed
    /// value otherwise. Identity-agnostic (case folding stays at the call site).
    /// </summary>
    public static bool TryResolveIdentityWrite(string? value, out string resolved)
    {
        resolved = "";
        if (value is null) return false;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;         // blank/empty → leave unchanged
        resolved = trimmed;
        return true;
    }
}

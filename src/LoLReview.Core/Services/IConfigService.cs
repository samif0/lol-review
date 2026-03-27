#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

/// <summary>
/// Read/write application configuration stored in %LOCALAPPDATA%\LoLReview\data\config.json.
/// Ported from Python config.py.
/// </summary>
public interface IConfigService
{
    /// <summary>Load config from disk (or return defaults if not found).</summary>
    Task<AppConfig> LoadAsync();

    /// <summary>Persist the full config to disk.</summary>
    Task SaveAsync(AppConfig config);

    // ── Convenience properties ──────────────────────────────────────

    string GithubToken { get; }
    string? AscentFolder { get; }
    bool TiltFixEnabled { get; }
    string ClipsFolder { get; }
    int ClipsMaxSizeMb { get; }
    bool BackupEnabled { get; }
    string BackupFolder { get; }
    Dictionary<string, string> Keybinds { get; }

    // ── Derived helpers ─────────────────────────────────────────────

    /// <summary>True if a valid Ascent recordings folder is configured.</summary>
    bool IsAscentEnabled { get; }

    /// <summary>
    /// Return the full keybind map, merging saved values with defaults
    /// for any missing keys.
    /// </summary>
    Dictionary<string, string> GetKeybinds();

    // ── Keybind metadata ────────────────────────────────────────────

    /// <summary>Default keybind assignments.</summary>
    static readonly IReadOnlyDictionary<string, string> DefaultKeybinds = AppConfig.DefaultKeybinds;

    /// <summary>Human-readable labels for keybind actions.</summary>
    static readonly IReadOnlyDictionary<string, string> KeybindLabels =
        new Dictionary<string, string>
        {
            { "play_pause",    "Play / Pause" },
            { "seek_fwd_5",    "Forward 5s" },
            { "seek_back_5",   "Back 5s" },
            { "seek_fwd_2",    "Forward 2s" },
            { "seek_back_2",   "Back 2s" },
            { "seek_fwd_10",   "Forward 10s" },
            { "seek_back_10",  "Back 10s" },
            { "seek_fwd_1",    "Forward 1s" },
            { "seek_back_1",   "Back 1s" },
            { "bookmark",      "Bookmark" },
            { "speed_up",      "Speed Up" },
            { "speed_down",    "Speed Down" },
            { "clip_in",       "Clip In" },
            { "clip_out",      "Clip Out" },
        };
}

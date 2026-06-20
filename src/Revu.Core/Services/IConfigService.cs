#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Read/write application configuration stored in %LOCALAPPDATA%\RevuData\config.json.
/// Ported from Python config.py.
/// </summary>
public interface IConfigService
{
    /// <summary>Load config from disk (or return defaults if not found).</summary>
    Task<AppConfig> LoadAsync();

    /// <summary>Persist the full config to disk.</summary>
    Task SaveAsync(AppConfig config);

    /// <summary>
    /// Explicitly clear the Riot session (token + email + expiry) — the ONLY path
    /// that wipes the stored session. A normal <see cref="SaveAsync"/> with an empty
    /// token / zero expiry now PRESERVES the stored session (it treats "empty" as
    /// "this writer didn't touch the session"), so deliberate sign-out goes through
    /// here. Used by POST /api/auth/logout and the guarded clear-partial path.
    /// </summary>
    Task ClearSessionAsync();

    // ── Convenience properties ──────────────────────────────────────

    string GithubToken { get; }
    string? AscentFolder { get; }

    /// <summary>P-009: unvalidated Ascent folder exactly as stored in config —
    /// for diagnostics that must distinguish "never set" from "set but failed
    /// validation" (the validated <see cref="AscentFolder"/> is null for both).</summary>
    string AscentFolderRaw { get; }
    bool TiltFixEnabled { get; }
    string ClipsFolder { get; }
    int ClipsMaxSizeMb { get; }
    bool BackupEnabled { get; }
    string BackupFolder { get; }
    Dictionary<string, string> Keybinds { get; }

    // Riot proxy session (from Path B login flow)
    string RiotSessionToken { get; }
    string RiotSessionEmail { get; }
    long RiotSessionExpiresAt { get; }
    string RiotId { get; }
    string RiotRegion { get; }
    string RiotPuuid { get; }
    string PrimaryRole { get; }
    bool OnboardingSkipped { get; }
    bool AscentReminderDismissed { get; }
    bool SidebarAnimationEnabled { get; }
    bool MinimizeDuringGame { get; }

    /// <summary>v2.17.8: auto-fill Timeline Inbox from derived game events.</summary>
    bool AutoTimelineClippingEnabled { get; }

    /// <summary>v2.17.8: user permanently hid the VOD-viewer hint about the toggle.</summary>
    bool AutoTimelineClippingHintDismissed { get; }

    // ── Derived helpers ─────────────────────────────────────────────

    /// <summary>True if onboarding should NOT be shown at startup.</summary>
    bool OnboardingComplete { get; }

    /// <summary>True if a valid Ascent recordings folder is configured.</summary>
    bool IsAscentEnabled { get; }

    /// <summary>True if the user has an unexpired session token.</summary>
    bool HasValidRiotSession { get; }

    /// <summary>True when logged in AND Riot ID + region are set (enables backfill).</summary>
    bool RiotProxyEnabled { get; }

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

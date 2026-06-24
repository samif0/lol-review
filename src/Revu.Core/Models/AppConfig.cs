#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// Application configuration stored in %LOCALAPPDATA%\RevuData\config.json.
/// </summary>
public class AppConfig
{
    /// <summary>
    /// Legacy plaintext GitHub token field. ConfigService migrates this into
    /// protected storage and writes an empty value back to config.json.
    /// </summary>
    public string GithubToken { get; set; } = "";
    public string AscentFolder { get; set; } = "";
    public Dictionary<string, string> Keybinds { get; set; } = new();
    public bool TiltFixMode { get; set; }
    public string ClipsFolder { get; set; } = "";
    public int ClipsMaxSizeMb { get; set; } = 2048;
    public bool BackupEnabled { get; set; }
    public string BackupFolder { get; set; } = "";
    public bool RequireReviewNotes { get; set; }

    // ── Riot API proxy (Path B: session-based auth) ─────────────────
    // Session token is issued by POST /auth/verify after the user enters an
    // invite code, receives a magic-link email, and pastes the one-time code
    // back into the app. Stored here so the app can authenticate on restart.

    /// <summary>
    /// Legacy plaintext session bearer token. ConfigService migrates this into
    /// protected storage and writes an empty value back to config.json.
    /// </summary>
    public string RiotSessionToken { get; set; } = "";

    /// <summary>Email the user signed up with. Displayed in Settings as "Logged in as X".</summary>
    public string RiotSessionEmail { get; set; } = "";

    /// <summary>Unix seconds when the current session expires. 0 if no session.</summary>
    public long RiotSessionExpiresAt { get; set; }

    /// <summary>Riot ID in the form <c>gameName#tagLine</c>.</summary>
    public string RiotId { get; set; } = "";

    /// <summary>Platform id (na1, euw1, kr, ...). See proxy's region map for valid values.</summary>
    public string RiotRegion { get; set; } = "";

    /// <summary>Resolved PUUID for the Riot ID. Cached so we don't hit /account on every scan.</summary>
    public string RiotPuuid { get; set; } = "";

    /// <summary>
    /// User's primary role in the game. Stored in Riot's internal format so it
    /// matches LCU's <c>assignedPosition</c> verbatim: <c>TOP|JUNGLE|MIDDLE|BOTTOM|UTILITY</c>.
    /// UI-facing labels (ADC, Support) map to BOTTOM and UTILITY respectively.
    /// </summary>
    public string PrimaryRole { get; set; } = "";

    /// <summary>True if the user dismissed the onboarding flow (LCU-only mode).</summary>
    public bool OnboardingSkipped { get; set; }

    // (v2.18 BenchmarkRank removed per P-005 — the rank-benchmark feature was
    // rejected by the user. A stale benchmark_rank key in config.json is
    // ignored harmlessly by the deserializer.)

    /// <summary>
    /// True if the user dismissed the Dashboard reminder to point at an
    /// Ascent recordings folder. The reminder only shows when this is false
    /// AND no folder is configured.
    /// </summary>
    public bool AscentReminderDismissed { get; set; }

    /// <summary>
    /// v2.15.0: sidebar page-enter animation. Some users find it distracting.
    /// Default true to keep the existing feel for anyone who hasn't touched
    /// the toggle.
    /// </summary>
    public bool SidebarAnimationEnabled { get; set; } = true;

    /// <summary>
    /// v2.16.1: minimize the window + suspend always-on UI animations
    /// (sidebar trails, ring pulses) while a League game is in progress.
    /// Restores both on game end. Cuts steady-state GPU/CPU during play.
    /// </summary>
    public bool MinimizeDuringGame { get; set; } = true;

    /// <summary>
    /// v2.17.8: when true (default), VodPlayerViewModel auto-fills the
    /// Timeline Inbox from inferred game events (kills, deaths, objective
    /// fights, teamfight clusters) via <c>SyncEvidenceCandidatesAsync</c>.
    /// Setting false stops the inbox auto-fill but leaves the colored
    /// timeline regions visible on the scrubber — the user just doesn't
    /// get a Needs-Review queue out of them.
    /// Default true preserves existing behavior for everyone who relies
    /// on the inbox today.
    /// </summary>
    public bool AutoTimelineClippingEnabled { get; set; } = true;

    /// <summary>
    /// v2.17.8: user permanently hid the VOD-viewer hint that explains the
    /// auto-clipping toggle. Mirrors <see cref="AscentReminderDismissed"/>.
    /// </summary>
    public bool AutoTimelineClippingHintDismissed { get; set; }

    /// <summary>
    /// When true, the VOD player shows an "Auto-clip objectives" button that
    /// batch-extracts a ~45s clip (30s before to 15s after) around every event
    /// tied to the user's active learning objectives. OPT-IN (default false):
    /// generating clips is ffmpeg + disk heavy, so the user turns it on
    /// deliberately. The toggle only GATES the button — nothing fires
    /// automatically. The button itself is on-demand.
    /// (Distinct from the orphaned <see cref="AutoTimelineClippingEnabled"/>,
    /// which referenced a now-deleted WinUI inbox-fill path.)
    /// </summary>
    public bool AutoClipObjectivesEnabled { get; set; }

    /// <summary>
    /// First-review tutorial progress. Empty step means it has not been started.
    /// Stored in config so the guided flow survives app restarts and real game waits.
    /// </summary>
    public string FirstReviewTutorialStep { get; set; } = "";

    public bool FirstReviewTutorialCompleted { get; set; }

    public bool FirstReviewTutorialDismissed { get; set; }

    public long FirstReviewTutorialObjectiveId { get; set; }

    public long FirstReviewTutorialGameId { get; set; }

    /// <summary>
    /// Default keybind map — each action maps to a key-event string.
    /// Users can remap these in Settings.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultKeybinds =
        new Dictionary<string, string>
        {
            { "play_pause",    "space" },
            { "seek_fwd_5",    "Right" },
            { "seek_back_5",   "Left" },
            { "seek_fwd_2",    "Shift-Right" },
            { "seek_back_2",   "Shift-Left" },
            { "seek_fwd_10",   "Control-Right" },
            { "seek_back_10",  "Control-Left" },
            { "seek_fwd_1",    "Alt-Right" },
            { "seek_back_1",   "Alt-Left" },
            { "bookmark",      "b" },
            { "speed_up",      "bracketright" },
            { "speed_down",    "bracketleft" },
            { "clip_in",       "i" },
            { "clip_out",      "o" },
        };
}

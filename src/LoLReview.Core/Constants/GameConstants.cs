#nullable enable

using System.Collections.Frozen;

namespace LoLReview.Core.Constants;

/// <summary>
/// Shared constants and utility functions used across the application.
/// Ported from Python constants.py.
/// </summary>
public static class GameConstants
{
    // ── Game mode filtering ──────────────────────────────────────────────

    /// <summary>
    /// Game modes that don't count as "real" games for session tracking / stats.
    /// </summary>
    public static readonly FrozenSet<string> CasualModes = new HashSet<string>
    {
        "ARAM", "CHERRY", "KIWI", "ULTBOOK", "TUTORIAL", "PRACTICETOOL"
    }.ToFrozenSet();

    /// <summary>
    /// Queue type labels (from StatsExtractor.GetQueueLabel) that are non-ranked and should
    /// be skipped by the review flow. Ranked Solo/Flex are the only reviewable queues.
    /// </summary>
    public static readonly FrozenSet<string> CasualQueueTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Normal Draft",
        "Normal Blind",
        "Quickplay",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SQL fragment for excluding casual modes and hidden games in queries.
    /// </summary>
    public static readonly string CasualModeSqlFilter =
        "AND COALESCE(is_hidden, 0) = 0 AND game_mode NOT IN ("
        + string.Join(",", CasualModes.Order().Select(m => $"'{m}'"))
        + ")";

    // ── Timing / intervals ───────────────────────────────────────────────

    /// <summary>How often the LCU monitor polls for game state (seconds).</summary>
    public const double GameMonitorPollIntervalS = 5.0;

    /// <summary>How often live events are polled during a game (seconds).</summary>
    public const double LiveEventPollIntervalS = 10.0;

    /// <summary>Thread join timeout when stopping the monitor (seconds).</summary>
    public const int MonitorStopTimeoutS = 5;

    /// <summary>Retries for fetching end-of-game stats.</summary>
    public const int EogStatsRetryAttempts = 12;

    /// <summary>Delay between end-of-game stats retries (seconds).</summary>
    public const int EogStatsRetryDelayS = 2;

    /// <summary>UI auto-refresh interval (session page, overlay) in milliseconds.</summary>
    public const int AutoRefreshIntervalMs = 30_000;

    /// <summary>Loss-streak flash animation interval in milliseconds.</summary>
    public const int FlashWarningIntervalMs = 500;

    /// <summary>Delay before scanning for VOD files on startup (milliseconds).</summary>
    public const int StartupVodScanDelayMs = 3_000;

    /// <summary>Retry VOD match after Ascent encoding (~90s) in milliseconds.</summary>
    public const int VodRetryDelayMs = 90_000;

    /// <summary>Delay before restarting after update install (milliseconds).</summary>
    public const int UpdateRestartDelayMs = 1_500;

    // ── Game thresholds ──────────────────────────────────────────────────

    /// <summary>Games shorter than 5 min are treated as remakes (seconds).</summary>
    public const int RemakeThresholdS = 300;

    /// <summary>Minimum game duration for session log rule checks (seconds).</summary>
    public const int SessionMinGameDurationS = 300;

    /// <summary>KDA >= this is displayed green.</summary>
    public const double KdaExcellentThreshold = 3.0;

    /// <summary>KDA >= this is displayed gold.</summary>
    public const double KdaGoodThreshold = 2.0;

    /// <summary>Minimum % of total damage to show in breakdown bar.</summary>
    public const double DamageDisplayThreshold = 0.02;

    // ── Mental / session ─────────────────────────────────────────────────

    /// <summary>Minimum mental rating value.</summary>
    public const int MentalRatingMin = 1;

    /// <summary>Maximum mental rating value.</summary>
    public const int MentalRatingMax = 10;

    /// <summary>Default mental rating value.</summary>
    public const int MentalRatingDefault = 5;

    /// <summary>Slider steps (max - min).</summary>
    public const int MentalRatingSteps = MentalRatingMax - MentalRatingMin;

    /// <summary>Mental >= 8 is green/excellent.</summary>
    public const int MentalExcellentThreshold = 8;

    /// <summary>Mental >= 5 is blue/decent (below = red).</summary>
    public const int MentalDecentThreshold = 5;

    /// <summary>Days of adherence to show "locked in".</summary>
    public const int AdherenceStreakLockedIn = 3;

    /// <summary>Consecutive losses before warning flash.</summary>
    public const int ConsecutiveLossWarning = 2;

    // ── Post-loss cooldown (Tice et al. 2001; Verduyn &amp; Lavrijsen 2015) ──

    /// <summary>Post-loss cooldown suggestion (seconds).</summary>
    public const int CooldownDurationS = 90;

    /// <summary>Breathing cycle interval (4s in, 4s out) in milliseconds.</summary>
    public const int CooldownBreatheIntervalMs = 4000;

    // ── Pre-game mood (Lieberman et al. 2007 — affect labeling) ───────────

    /// <summary>Pre-game mood labels keyed by numeric value (1-5).</summary>
    public static readonly IReadOnlyDictionary<int, string> MoodLabels =
        new Dictionary<int, string>
        {
            { 1, "Tilted" },
            { 2, "Off" },
            { 3, "Neutral" },
            { 4, "Good" },
            { 5, "Locked In" },
        };

    /// <summary>Pre-game mood colors keyed by numeric value (1-5).</summary>
    public static readonly IReadOnlyDictionary<int, string> MoodColors =
        new Dictionary<int, string>
        {
            { 1, "#ef4444" },
            { 2, "#f97316" },
            { 3, "#6b7280" },
            { 4, "#22c55e" },
            { 5, "#10b981" },
        };

    // ── Attribution (Weiner 1985; Dweck 2006) ─────────────────────────────

    /// <summary>Attribution options as (value, label) pairs.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> AttributionOptions =
    [
        ("my_play", "My play"),
        ("team_effort", "Team effort"),
        ("teammates", "Teammates"),
        ("external", "External"),
    ];

    // ── Display limits ────────────────────────────────────────────────────

    /// <summary>Default page size for game queries.</summary>
    public const int DefaultRecentGamesLimit = 50;

    /// <summary>Paginated history page size.</summary>
    public const int HistoryPageSize = 50;

    /// <summary>Days to look back for unreviewed games.</summary>
    public const int UnreviewedGamesDays = 3;

    /// <summary>Max unreviewed games shown on home page.</summary>
    public const int UnreviewedGamesDisplayLimit = 8;

    // ── VOD matching ──────────────────────────────────────────────────────

    /// <summary>15-minute window for matching VODs to games (seconds).</summary>
    public const int VodMatchWindowS = 900;

    /// <summary>Grace period for mtime fallback matching (seconds).</summary>
    public const int VodMtimeGraceS = 30;

    // ── Clip extraction ───────────────────────────────────────────────────

    /// <summary>Quality for re-encode fallback.</summary>
    public const int FfmpegCrf = 23;

    /// <summary>Default timeout for clip extraction (seconds).</summary>
    public const int FfmpegClipTimeoutS = 60;

    /// <summary>Timeout for re-encode fallback (seconds).</summary>
    public const int FfmpegReEncodeTimeoutS = 180;

    // ── Updater ───────────────────────────────────────────────────────────

    /// <summary>Timeout for checking for updates (seconds).</summary>
    public const int UpdateCheckTimeoutS = 10;

    /// <summary>Timeout for downloading updates (seconds).</summary>
    public const int UpdateDownloadTimeoutS = 60;

    /// <summary>Download chunk size in bytes (64 KB).</summary>
    public const int DownloadChunkSize = 64 * 1024;

    // ── VOD player ────────────────────────────────────────────────────────

    /// <summary>Available playback speeds.</summary>
    public static readonly IReadOnlyList<double> VodPlaybackSpeeds =
        [0.25, 0.5, 1.0, 1.5, 2.0];

    /// <summary>Position display update interval (milliseconds).</summary>
    public const int VodTimeUpdateIntervalMs = 250;

    /// <summary>Duration of error flash on invalid input (milliseconds).</summary>
    public const int VodErrorFlashMs = 1_500;

    /// <summary>Duration of "Saved!" feedback on clip save (milliseconds).</summary>
    public const int ClipSaveFeedbackMs = 3_000;

    // ── Helper methods ──────────────────────────────────────────────────

    /// <summary>Format game duration as MM:SS.</summary>
    public static string FormatDuration(int seconds) =>
        $"{seconds / 60}:{seconds % 60:D2}";

    /// <summary>Format large numbers with K suffix.</summary>
    public static string FormatNumber(int? n)
    {
        var value = n ?? 0;
        return value >= 1000
            ? $"{value / 1000.0:F1}k"
            : value.ToString();
    }
}

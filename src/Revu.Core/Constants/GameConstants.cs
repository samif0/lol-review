#nullable enable

using System.Collections.Frozen;

namespace Revu.Core.Constants;

/// <summary>
/// Shared constants and utility functions used across the application.
/// Ported from Python constants.py.
/// </summary>
public static class GameConstants
{
    private static readonly FrozenDictionary<int, string> QueueLabels = new Dictionary<int, string>
    {
        [420] = "Ranked Solo/Duo",
        [440] = "Ranked Flex",
        [400] = "Normal Draft",
        [430] = "Normal Blind",
        [490] = "Quickplay",
    }.ToFrozenDictionary();

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
    /// Queue types that are considered ranked and should appear in the app.
    /// Everything else is filtered out.
    /// </summary>
    public static readonly FrozenSet<string> RankedQueueTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Ranked Solo/Duo",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SQL fragment for excluding non-ranked, casual, and hidden games in queries.
    /// Only Ranked Solo/Duo and manually entered games pass this filter.
    /// </summary>
    public static readonly string CasualModeSqlFilter =
        "AND COALESCE(is_hidden, 0) = 0 AND COALESCE(queue_type, '') IN ('Ranked Solo/Duo', 'Manual')";

    // ── Timing / intervals ───────────────────────────────────────────────

    /// <summary>How often the LCU monitor polls for game state (seconds).</summary>
    public const double GameMonitorPollIntervalS = 5.0;

    /// <summary>How often the kill-feed event stream (/eventdata) is polled during a
    /// game (seconds). The event stream changes coarsely, so 10s is plenty.</summary>
    public const double LiveEventPollIntervalS = 10.0;

    /// <summary>How often YOUR champion HP is sampled (/activeplayer + /gamestats) during
    /// a game (seconds). Decoupled from — and much faster than — the event-stream poll so
    /// derived events that key off HP transitions (trades, recalls) anchor to within ~1s
    /// of when they happened, instead of smeared across a 10s window. All localhost.</summary>
    public const double HpSamplePollIntervalS = 1.0;

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

    /// <summary>
    /// v2.18 (P-007): VOD-match retry ladder after game end — delays between
    /// successive attempts. Ascent finalizes its file seconds-to-minutes after
    /// the game ends; a single 90s shot lost that race once and the recording
    /// stayed orphaned until the next startup scan. Attempts land ~1.5, ~4.5
    /// and ~9.5 minutes after EOG. In-memory only: app restarts are covered by
    /// the startup scan and the review-open rematch.
    /// </summary>
    public static readonly int[] VodRetryLadderMs = [90_000, 180_000, 300_000];

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

    /// <summary>
    /// Mental at or below this is "tilted" (matches the app's 1–3 TILTED
    /// analytics bucket). A game rated this low requires a cool-off break
    /// before queueing again — see <see cref="TiltCooloffSeconds"/>.
    /// </summary>
    public const int MentalTiltedFloor = 3;

    /// <summary>
    /// Required cool-off (seconds) after a tilted game (mental ≤
    /// <see cref="MentalTiltedFloor"/>) before the next game. The min_mental
    /// rule's behavioral streak reads this: rate a game tilted, then requeue
    /// inside this window, and that next game is the trip. 2h is the
    /// user-specified break (P-015).
    /// </summary>
    public const int TiltCooloffSeconds = 2 * 60 * 60;

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

    /// <summary>40-minute window for matching VODs to games (seconds).
    /// Ascent starts recording at queue/champ-select time, but Riot's gameCreation
    /// is at loading-screen start. Queue waits, champ select, and dodges can add
    /// 20-30+ minutes between the two timestamps.</summary>
    public const int VodMatchWindowS = 2400;

    /// <summary>Max positive delta (recording started AFTER gameCreation) for filename matching.
    /// Ascent normally starts before gameCreation, so a large positive delta means wrong file.
    /// 5 minutes covers minute-precision rounding plus minor clock drift.</summary>
    public const int VodFilenamePositiveSlackS = 300;

    /// <summary>Grace period for mtime fallback matching (seconds).</summary>
    public const int VodMtimeGraceS = 30;

    // ── Clip extraction ───────────────────────────────────────────────────

    /// <summary>Quality for re-encode fallback.</summary>
    public const int FfmpegCrf = 23;

    /// <summary>Default timeout for clip extraction (seconds).</summary>
    public const int FfmpegClipTimeoutS = 60;

    /// <summary>Timeout for re-encode fallback (seconds).</summary>
    public const int FfmpegReEncodeTimeoutS = 180;

    // ── Auto-clip objective events ────────────────────────────────────────
    // The VOD player's "Auto-clip objectives" button buffers each objective-tied
    // event into a ~45s clip: PreRoll before the event, PostRoll after.

    /// <summary>Seconds of lead-in before an objective event (clip start = event - this).</summary>
    public const int AutoClipPreRollS = 30;

    /// <summary>Seconds of trail after an objective event (clip end = event + this).</summary>
    public const int AutoClipPostRollS = 15;

    /// <summary>
    /// Minimum gap (seconds) between the START of two consecutive auto-clips. Events
    /// whose buffered start falls within this of the previous kept clip's start are
    /// skipped, collapsing heavily-overlapping windows (two events seconds apart would
    /// otherwise yield two near-identical 45s clips).
    /// </summary>
    public const int AutoClipMinGapS = 20;

    /// <summary>
    /// Hard cap on clips created per auto-clip invocation. Protects the clips folder
    /// size cap (oldest-first eviction would otherwise trim the user's manual clips)
    /// and bounds ffmpeg CPU. Excess events are reported as skipped.
    /// </summary>
    public const int AutoClipMaxPerCall = 12;

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

    /// <summary>Map a Riot queue id to a human-readable queue label when known.</summary>
    public static string GetQueueLabel(int queueId) =>
        QueueLabels.TryGetValue(queueId, out var label) ? label : "";

    /// <summary>Normalize queue labels stored as ids, raw queue names, or already-human labels.</summary>
    public static string NormalizeQueueLabel(string? queueType)
    {
        if (string.IsNullOrWhiteSpace(queueType))
        {
            return "";
        }

        var trimmed = queueType.Trim();
        if (int.TryParse(trimmed, out var queueId))
        {
            var mapped = GetQueueLabel(queueId);
            return string.IsNullOrWhiteSpace(mapped) ? trimmed : mapped;
        }

        return trimmed.ToUpperInvariant() switch
        {
            "RANKED_SOLO_5X5" => "Ranked Solo/Duo",
            "RANKED_SOLO" => "Ranked Solo/Duo",
            "RANKED FLEX" => "Ranked Flex",
            "RANKED_FLEX_SR" => "Ranked Flex",
            "NORMAL DRAFT" => "Normal Draft",
            "NORMAL BLIND" => "Normal Blind",
            _ when string.Equals(trimmed, "Ranked Solo", StringComparison.OrdinalIgnoreCase) => "Ranked Solo/Duo",
            _ => trimmed,
        };
    }

    // ── Champion name normalization ──────────────────────────────────────

    /// <summary>
    /// Riot's Data Dragon ships champion <c>id</c>s without apostrophes/spaces
    /// (e.g. "Kaisa", "MonkeyKing") but a separate display <c>name</c>
    /// ("Kai'Sa", "Wukong"). The LCU and EOG payloads aren't consistent about
    /// which one they hand us, so the same champion lands in champion_name under
    /// two spellings. This map repairs the known apostrophe/casing divergences
    /// to the Data Dragon DISPLAY name so per-champion grouping and labels agree.
    /// Keyed by the normalized group key (see <see cref="NormalizeChampionKey"/>).
    /// </summary>
    private static readonly FrozenDictionary<string, string> ChampionDisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kaisa"]        = "Kai'Sa",
            ["khazix"]       = "Kha'Zix",
            ["chogath"]      = "Cho'Gath",
            ["velkoz"]       = "Vel'Koz",
            ["reksai"]       = "Rek'Sai",
            ["kogmaw"]       = "Kog'Maw",
            ["belveth"]      = "Bel'Veth",
            ["nunuwillump"]  = "Nunu & Willump",
            ["drmundo"]      = "Dr. Mundo",
            ["leblanc"]      = "LeBlanc",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Collapse a champion display name to a stable grouping key by stripping the
    /// punctuation/whitespace that varies between sources (apostrophes, spaces,
    /// periods, ampersands) and case-folding. "Kai'Sa" and "Kaisa" both become
    /// "kaisa" so any per-champion aggregate counts them as one champion.
    /// Returns "" for null/blank input.
    /// </summary>
    public static string NormalizeChampionKey(string? championName)
    {
        if (string.IsNullOrWhiteSpace(championName))
        {
            return "";
        }

        var span = championName.AsSpan().Trim();
        var sb = new System.Text.StringBuilder(span.Length);
        foreach (var ch in span)
        {
            // Keep only letters/digits; drop apostrophes (' ʼ ’), spaces, periods,
            // ampersands, hyphens — exactly the characters that differ between the
            // Data Dragon id form and the display form.
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolve the canonical user-facing champion name. Repairs the known
    /// id-vs-display divergences (e.g. "Kaisa" -&gt; "Kai'Sa") so aggregated rows
    /// carry the proper display label; falls back to the trimmed input for
    /// champions not in the repair map (their spelling is already canonical).
    /// </summary>
    public static string CanonicalChampionName(string? championName)
    {
        if (string.IsNullOrWhiteSpace(championName))
        {
            return "";
        }

        var key = NormalizeChampionKey(championName);
        return ChampionDisplayNames.TryGetValue(key, out var display)
            ? display
            : championName.Trim();
    }

    /// <summary>Resolve the best user-facing label for a game using queue info when available.</summary>
    public static string GetDisplayGameMode(string? gameMode, string? queueType)
    {
        var normalizedQueue = NormalizeQueueLabel(queueType);
        if (!string.IsNullOrWhiteSpace(normalizedQueue))
        {
            return normalizedQueue;
        }

        if (string.IsNullOrWhiteSpace(gameMode))
        {
            return "";
        }

        var trimmed = gameMode.Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "CLASSIC" => "Ranked Solo/Duo",
            _ => trimmed,
        };
    }
}

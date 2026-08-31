#nullable enable

namespace Revu.Core.Constants;

/// <summary>
/// The pattern-detection vocabulary and tunables, in ONE file shared by the
/// writer (<c>PatternEvidenceMaterializer</c>) and the reader
/// (<c>EvidenceRepository.GetPatternCardsAsync</c> / <c>GetPatternMomentsAsync</c>).
///
/// <para>
/// HISTORY: the original detectors string-matched evidence titles that only the
/// removed WinUI app ever wrote (its deletion in the Tauri migration severed the
/// producer), so the Patterns page sat empty for months while its tests passed on
/// hand-fed legacy fixtures. Keeping every matched title, source key, threshold,
/// and window here — and pinning materializer output against the queries in
/// tests — is what prevents that reader/writer drift from recurring.
/// </para>
///
/// <para>
/// Titles double as detection keys because <c>title</c> is the ONE column that
/// survives the note-flow clip promotion (<c>AttachClipToEvidenceAsync</c>
/// rewrites source_kind/source_key/times but preserves title), so a moment the
/// user annotated stays in its pattern playlist.
/// </para>
/// </summary>
public static class PatternConstants
{
    /// <summary>
    /// Recency window (days) shared by every card count, every moment playlist,
    /// and the materializer backfill. At 2-5 games/day this holds ~30-70 games:
    /// every threshold is reachable within days of normal play, old habits age
    /// out instead of accumulating forever, and WinUI-era legacy rows stay fenced
    /// off. games.timestamp is unix seconds (same comparison
    /// DeathClassificationsRepository.CutoffFor ships).
    /// </summary>
    public const int WindowDays = 14;

    /// <summary>
    /// Re-arm hysteresis: a reviewed pattern comes back as pending only once at
    /// least this many NEW moments (created after the review instant) accrue —
    /// one fresh moment must not re-nag a pattern the user just worked through.
    /// </summary>
    public const int ReArmNewMoments = 2;

    /// <summary>Max cards in a patterns snapshot (severity-then-count ordered).</summary>
    public const int PatternCardLimit = 6;

    /// <summary>
    /// Candidate fetch size for the snapshot builders — every card the seven
    /// detectors can emit (at most 11 under their per-kind limits), so the
    /// review gate runs over the FULL candidate set and a reviewed-closed card
    /// can never crowd a pending one out of the display cap.
    /// </summary>
    public const int PatternCandidateLimit = 12;

    // ── Per-kind thresholds ─────────────────────────────────────────────────
    // All counts are within the window. "Share" thresholds divide by a window
    // denominator so heavy weeks don't spuriously fire and light weeks still can.

    public const int DeathClassMinCount = 4;
    public const int DeathClassMinGames = 2;
    public const double DeathClassMinShare = 0.30;   // of classified deaths in window
    public const int DeathClassHighCount = 8;
    public const double DeathClassHighShare = 0.50;
    public const int DeathClassCardLimit = 2;

    public const int GankMinCount = 3;
    public const int GankMinGames = 2;

    public const int LostFightMinCount = 3;
    public const int LostFightMinGames = 2;

    public const int DeathBeforeObjMinCount = 3;
    public const int DeathBeforeObjMinGames = 2;

    public const int BadObjectiveMinBad = 2;
    public const int BadObjectiveHighBad = 5;
    public const int BadObjectiveCardLimit = 3;

    public const int TagMinCount = 3;
    public const double TagMinShare = 0.30;          // of reviewed games in window
    public const double TagHighShare = 0.50;
    public const int TagCardLimit = 2;

    public const int RuleBreakMinCount = 3;
    public const int RuleBreakHighCount = 5;

    // ── Detected pattern kinds (card.Kind / pattern_reviews.kind values) ────

    public const string KindDeathClassMix = "death_class_mix";
    public const string KindGankDeaths = "gank_deaths";
    public const string KindLostObjectiveFights = "lost_objective_fights";
    public const string KindDeathsBeforeObjectives = "deaths_before_objectives";
    public const string KindBadObjectiveEvidence = "bad_objective_evidence";
    public const string KindRecurringConceptTag = "recurring_concept_tag";
    public const string KindRuleBreaks = "rule_breaks";

    // ── Titles the materializer writes and the detectors match ──────────────
    // The two legacy LIKE families ('Lost % fight%', 'Death before %') are also
    // written by TimelineInferenceService region names; the exact-title kinds
    // below belong entirely to the materializer.

    /// <summary>Laning-phase death with the enemy jungler on the kill
    /// (game_events DEATH with Details.jungle_gank stamped at capture).</summary>
    public const string GankDeathTitle = "Death to gank";

    /// <summary>Prefix of a death-audit moment title; the full title is
    /// <c>"Death: {chip label}"</c> (e.g. "Death: GREED") — no timestamp, so the
    /// death_class_mix GROUP BY title works.</summary>
    public const string DeathAuditTitlePrefix = "Death: ";

    /// <summary>Title a promoted death-audit clip is renamed to when the user
    /// CLEARS the classification: keeps their clip + note, leaves every count.</summary>
    public const string ClearedDeathAuditTitle = "Death";

    /// <summary>Game-level rule-break anchor title (one per rule-broken game).</summary>
    public const string RuleBreakTitle = "Broke a queue rule";

    public static string DeathAuditTitle(string chipLabel) => $"{DeathAuditTitlePrefix}{chipLabel}";

    // ── Source keys (dedupe identity under idx_evidence_items_source_key) ───

    public const string RuleBreakSourceKey = "rulebreak";
    public const string DeathAuditSourceKeyPrefix = "death-audit:";
    public const string GankDeathSourceKeyPrefix = "gank-death:";
    public const string TagSourceKeyPrefix = "tag:";

    public static string DeathAuditSourceKey(int gameTimeSeconds) => $"{DeathAuditSourceKeyPrefix}{gameTimeSeconds}";
    public static string GankDeathSourceKey(int gameTimeSeconds) => $"{GankDeathSourceKeyPrefix}{gameTimeSeconds}";
    public static string TagSourceKey(long tagId) => $"{TagSourceKeyPrefix}{tagId}";

    /// <summary>Padding around a point death for its moment window (mirrors the
    /// first-combat region shape: 6s lead / 8s trail).</summary>
    public const int DeathMomentLeadSeconds = 6;
    public const int DeathMomentTrailSeconds = 8;
}

/// <summary>
/// Structured kinds stamped onto <c>InferredTimelineRegion</c>s so the
/// materializer selects pattern-relevant regions STRUCTURALLY instead of parsing
/// display names (the parse-the-name approach is how the reader and writer
/// drifted apart last time).
/// </summary>
public static class PatternRegionKinds
{
    /// <summary>An objective fight whose combat outcome was "Lost"
    /// (name "Lost Dragon/Baron/Herald fight").</summary>
    public const string LostObjectiveFight = "lost_objective_fight";

    /// <summary>A player death 15-75s before a major objective
    /// (name "Death before Dragon/Baron/Herald").</summary>
    public const string DeathBeforeObjective = "death_before_objective";
}

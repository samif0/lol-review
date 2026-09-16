#nullable enable

using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Constants;

/// <summary>
/// Cross-pattern review uses saved clips and bookmarks explicitly attached to
/// active objectives. All saved history is available for revision; only recent
/// bad examples across distinct games are called a recurring mistake. Legacy
/// event/criterion constants remain for timeline materialization and old review
/// keys, but automatic anchors are not cross-pattern review candidates.
/// </summary>
public static class PatternConstants
{
    /// <summary>
    /// Recency window for mistake trends and timeline materializer backfill.
    /// Saved-objective revision collections deliberately retain all history.
    /// </summary>
    public const int WindowDays = 14;

    /// <summary>
    /// Re-arm hysteresis: a reviewed pattern comes back as pending only once at
    /// least this many NEW moments (created after the review instant) accrue.
    /// </summary>
    public const int ReArmNewMoments = 2;

    /// <summary>Max cards in bounded dashboard summaries.</summary>
    public const int PatternCardLimit = 6;

    /// <summary>
    /// Legacy display cap retained for callers that need a bounded preview.
    /// Full saved-review playlists can be paginated without discarding history.
    /// </summary>
    public const int PatternMomentDisplayLimit = 24;

    /// <summary>
    /// Candidate fetch size for bounded dashboard summaries. The full Patterns
    /// surface requests every objective collection and handles pagination.
    /// </summary>
    public const int PatternCandidateLimit = 12;

    // ── Per-kind thresholds (window-scoped counts) ──────────────────────────

    public const int BadObjectiveMinBad = 2;
    public const int BadObjectiveMinGames = 2;
    public const int BadObjectiveHighBad = 5;
    public const int BadObjectiveCardLimit = 3;

    /// <summary>Legacy objective-criteria thresholds, retained for compatibility.
    /// Failed-criterion anchors no longer produce cross-pattern cards.</summary>
    public const int ObjCritMinFails = 3;
    public const double ObjCritMinFailShare = 0.5;
    public const double ObjCritHighFailShare = 0.75;
    public const int ObjCritCardLimit = 3;

    /// <summary>Legacy event-recurrence thresholds, retained for compatibility.
    /// Raw event counts no longer produce cross-pattern cards.</summary>
    public const int ObjEventMinCount = 5;
    public const int ObjEventMinGames = 3;
    public const int ObjEventHighCount = 10;
    public const int ObjEventCardLimit = 3;

    // ── Detected pattern kinds (card.Kind / pattern_reviews.kind values) ────

    public const string KindBadObjectiveEvidence = "bad_objective_evidence";
    public const string KindSavedObjectiveEvidence = "saved_objective_evidence";
    public const string KindObjectiveCriteria = "objective_criteria";
    public const string KindObjectiveEvents = "objective_events";

    // ── Source keys (dedupe identity under idx_evidence_items_source_key) ───

    /// <summary>
    /// The polarity the materializer stamps on a tracked-event anchor. Deaths
    /// (and their derived gank/fog attributes) read as bad; everything else a
    /// player might track (kills, objectives, trades, casts) is neutral —
    /// recurrence is the signal, not blame. Shared with EvidenceAutoAnchors so a
    /// polarity the USER changed is recognised as a triage action.
    /// </summary>
    public static string DefaultAnchorPolarity(string token) => Canonical(token) switch
    {
        GameEvent.EventTypes.Death => EvidencePolarities.Bad,
        GameEvent.TrackableTokens.JungleGankToken => EvidencePolarities.Bad,
        GameEvent.TrackableTokens.FogDeathToken => EvidencePolarities.Bad,
        // Committing while outnumbered is the decision the numbers filter exists to catch.
        GameEvent.TrackableTokens.OutnumberedTeamfightToken => EvidencePolarities.Bad,
        _ => EvidencePolarities.Neutral,
    };

    /// <summary>The token of an <c>objev:{TOKEN}:{anchor}</c> key, "" for any other key.</summary>
    public static string TokenFromObjEventKey(string? sourceKey)
    {
        if (sourceKey is null || !sourceKey.StartsWith(ObjEventSourceKeyPrefix, StringComparison.Ordinal)) return "";
        var rest = sourceKey.AsSpan(ObjEventSourceKeyPrefix.Length);
        var colon = rest.IndexOf(':');
        return colon <= 0 ? "" : Canonical(rest[..colon].ToString());
    }

    /// <summary>One timeline anchor per (game, tracked token, event second):
    /// <c>objev:{TOKEN}:{timeS}</c>. These objective-agnostic anchors never enter
    /// saved cross-pattern review collections unless explicitly saved as clips.</summary>
    public const string ObjEventSourceKeyPrefix = "objev:";

    /// <summary>One anchor per (game, objective) failed structured criterion:
    /// <c>objcrit:{objectiveId}</c>. These remain timeline evidence; they are
    /// not playable saved moments and do not enter cross-pattern collections.</summary>
    public const string ObjCritSourceKeyPrefix = "objcrit:";

    public static string ObjEventSourceKeyForToken(string token) =>
        $"{ObjEventSourceKeyPrefix}{Canonical(token)}:";

    public static string ObjEventSourceKey(string token, int gameTimeSeconds) =>
        $"{ObjEventSourceKeyForToken(token)}{gameTimeSeconds}";

    public static string ObjCritSourceKey(long objectiveId) => $"{ObjCritSourceKeyPrefix}{objectiveId}";

    /// <summary>Canonical token spelling (UPPER, trimmed) — objective_event_types
    /// stores tokens this way and source keys must match it exactly.</summary>
    public static string Canonical(string token) => (token ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// Display label (and evidence title) for a trackable token, from the one
    /// token catalog the timeline uses. Cross-pattern review never infers an
    /// objective association from this label.
    /// </summary>
    public static string TokenLabel(string token)
    {
        var canonical = Canonical(token);
        foreach (var entry in GameEvent.TrackableTokens.Catalog)
        {
            if (string.Equals(entry.Token, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Label;
            }
        }
        if (canonical.StartsWith(GameEvent.TrackableTokens.SpellPrefix, StringComparison.Ordinal))
        {
            // Legacy per-spell tokens dropped from the Catalog but still valid.
            var spell = canonical[GameEvent.TrackableTokens.SpellPrefix.Length..];
            return spell.Length == 0 ? "Spell cast"
                : char.ToUpperInvariant(spell[0]) + spell[1..].ToLowerInvariant() + " cast";
        }
        return canonical.Length == 0 ? "" : char.ToUpperInvariant(canonical[0]) + canonical[1..].ToLowerInvariant();
    }

    /// <summary>Display title for a failed-criterion timeline anchor.</summary>
    public static string ObjCritTitle(string objectiveTitle) => $"Missed: {objectiveTitle}";

    /// <summary>Padding around a point event for its moment window.</summary>
    public const int MomentLeadSeconds = 6;
    public const int MomentTrailSeconds = 8;

    /// <summary>Teamfight-cluster anchor shape (mirrors ObjectiveEventTieResolver's
    /// membership rule: ≥3 combat events chained within 14s gaps).</summary>
    public const int TeamfightGapSeconds = 14;
    public const int TeamfightMinEvents = 3;
    public const int TeamfightLeadSeconds = 4;
    public const int TeamfightTrailSeconds = 6;

    /// <summary>Stored-fight detection over the Match-V5 timeline (TeamfightAnalyzer):
    /// a cluster needs this many champion kills, and a kill joins a cluster only when
    /// it lands within this many map units of a kill already in it (the map is
    /// ~14,870 units square; 3,000 is ~14 s of walking, so two skirmishes on opposite
    /// sides of the map at the same second stay two fights).</summary>
    public const int TeamfightMinKills = 3;
    public const int TeamfightLinkRadiusUnits = 3000;

    /// <summary>
    /// Source-key prefixes of the RETIRED v3.5 materialized rows (gank deaths,
    /// death audits, tag anchors, rule breaks, inferred regions). Materializer
    /// v2 deletes un-promoted, un-noted rows under these keys so the retired
    /// kinds don't linger in per-game evidence lists; anything the user noted
    /// or promoted to a clip is preserved.
    /// </summary>
    public static readonly string[] RetiredSourceKeyPrefixes =
    [
        "gank-death:",
        "death-audit:",
        "tag:",
        "objective:",
        "objective-death:",
    ];

    /// <summary>Exact retired source keys (no prefix family).</summary>
    public static readonly string[] RetiredSourceKeys = ["rulebreak"];
}

/// <summary>
/// Structured kinds stamped onto <c>InferredTimelineRegion</c>s. The v3.6
/// objective-only detectors no longer consume them, but the stamps stay: they
/// are the obvious structured feeder for the planned exploratory ("novel
/// pattern") detection away from learning objectives, and selecting regions by
/// Kind — never by parsing display names — is the discipline that keeps a
/// future reader from drifting away from this writer.
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

#nullable enable

namespace Revu.Core.Data.Repositories;

public static class EvidenceKinds
{
    public const string Clip = "clip";
    public const string TimelineRegion = "timeline_region";
    public const string ReviewNote = "review_note";
    public const string PromptAnswer = "prompt_answer";
    public const string MatchupNote = "matchup_note";

    public static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        Clip => Clip,
        TimelineRegion => TimelineRegion,
        ReviewNote => ReviewNote,
        PromptAnswer => PromptAnswer,
        MatchupNote => MatchupNote,
        _ => TimelineRegion,
    };
}

public static class EvidencePolarities
{
    public const string Good = "good";
    public const string Neutral = "neutral";
    public const string Bad = "bad";

    public static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        Good => Good,
        Bad => Bad,
        _ => Neutral,
    };
}

public static class EvidenceStatuses
{
    public const string NeedsReview = "needs_review";
    public const string Evidence = "evidence";
    public const string Dismissed = "dismissed";
    public const string Highlight = "highlight";

    public static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        Evidence => Evidence,
        Dismissed => Dismissed,
        Highlight => Highlight,
        _ => NeedsReview,
    };
}

/// <summary>
/// The rows the post-game pass writes on its own (<c>objev:</c> tracked-event
/// anchors, <c>objcrit:</c> failed-criterion anchors — PatternEvidenceMaterializer).
/// They exist so the Patterns page can count recurrence; whether they ALSO show
/// up as review moments on the Review page and in the VOD inbox is the user's
/// "Auto-fill Timeline Inbox from game events" setting. An anchor stops being
/// "untouched" the moment the user does anything to it — notes it, triages it
/// (dismiss / highlight), tags it to an objective, prompt or concept, or the note
/// flow promotes it to a clip (source_kind flips to 'clip') — and then it stays
/// visible regardless of the setting.
/// </summary>
public static class EvidenceAutoAnchors
{
    private const string ObjEventPrefix = "objev:";
    private const string ObjCritPrefix = "objcrit:";

    public static bool IsAutoAnchorKey(string? sourceKey) =>
        sourceKey is not null
        && (sourceKey.StartsWith(ObjEventPrefix, StringComparison.Ordinal)
            || sourceKey.StartsWith(ObjCritPrefix, StringComparison.Ordinal));

    public static bool IsUntouched(EvidenceItemRecord row) =>
        string.Equals(row.SourceKind, EvidenceKinds.TimelineRegion, StringComparison.OrdinalIgnoreCase)
        && IsAutoAnchorKey(row.SourceKey)
        && string.IsNullOrWhiteSpace(row.Note)
        && string.Equals(row.Status, EvidenceStatuses.Evidence, StringComparison.OrdinalIgnoreCase)
        && row.ObjectiveId is null
        && row.PromptId is null
        && row.ConceptTagId is null;

    /// <summary>The rows a per-game surface should show given the setting.</summary>
    public static IReadOnlyList<EvidenceItemRecord> ForSurface(IReadOnlyList<EvidenceItemRecord> rows, bool autoFillEnabled) =>
        autoFillEnabled ? rows : rows.Where(static r => !IsUntouched(r)).ToList();
}

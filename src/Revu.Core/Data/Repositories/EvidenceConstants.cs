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

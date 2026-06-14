#nullable enable

namespace Revu.Core.Data.Repositories;

public sealed record ConceptTagRecord(
    long Id,
    string Name,
    string Polarity,
    string Color);

public sealed record ObjectiveSummary(
    long Id,
    string Title,
    string SkillArea,
    string Type,
    string CompletionCriteria,
    string Description,
    string Phase,
    string Status,
    bool IsPriority,
    int Score,
    int GameCount,
    long? CreatedAt,
    long? CompletedAt,
    // v2.15.0: multi-phase practice bools. An objective can practice
    // any subset of {pre, in, post}. Phase (above) is kept for backwards
    // compatibility — it reflects the first set bool in pre→in→post order.
    bool PracticePre = false,
    bool PracticeIn = false,
    bool PracticePost = false,
    // v2.17.7: mini-objective target game count. 0 = no target (primary).
    int TargetGameCount = 0,
    // v2.18 (F2): game-phase focus for auto-clip matching. '' = auto-infer from title.
    string FocusPhase = "",
    // v2.18 (schema v5): structured, machine-checkable criterion. Empty metric
    // means the objective only has the free-text CompletionCriteria (if any).
    string CriteriaMetric = "",
    string CriteriaOp = ">=",
    double CriteriaValue = 0)
{
    public bool IsMini => string.Equals(Type, "mini", StringComparison.OrdinalIgnoreCase);
    public int GamesRemaining => Math.Max(0, TargetGameCount - GameCount);
    public bool IsMiniComplete => IsMini && TargetGameCount > 0 && GameCount >= TargetGameCount;
    public bool HasStructuredCriteria => !string.IsNullOrWhiteSpace(CriteriaMetric);
}

public sealed record GameObjectiveRecord(
    long GameId,
    long ObjectiveId,
    bool Practiced,
    string ExecutionNote,
    string Title,
    string CompletionCriteria,
    string Type,
    bool IsPriority,
    string Phase,
    // v2.18 (schema v5): outcome of the structured criterion for this game.
    // null = not evaluated (no criterion, or stat unavailable).
    int? CriteriaMet = null,
    string CriteriaMetric = "",
    string CriteriaOp = ">=",
    double CriteriaValue = 0);

public sealed record MatchupNoteRecord(
    long Id,
    string Champion,
    string Enemy,
    string Note,
    int? Helpful,
    long? GameId,
    long? CreatedAt);

public sealed record VodSummary(
    long Id,
    long GameId,
    string FilePath,
    long FileSize,
    int DurationSeconds,
    long? MatchedAt);

public sealed record VodBookmarkRecord(
    long Id,
    long GameId,
    int GameTimeSeconds,
    string Note,
    string TagsJson,
    int? ClipStartSeconds,
    int? ClipEndSeconds,
    string ClipPath,
    string Quality,
    long? CreatedAt,
    long? ObjectiveId,
    // v2.15.7: optional prompt tag. When set, post-game routes this clip's
    // [MM:SS] note into the prompt's answer field instead of (or in addition
    // to) the parent objective's General Notes.
    long? PromptId = null,
    // Public share link (revu.lol/<id>) once the clip has been uploaded. Empty
    // until shared. Late-added column; tolerated as missing on older DBs.
    string ShareUrl = "");

public sealed record RuleRecord(
    long Id,
    string Name,
    string Description,
    string RuleType,
    string ConditionValue,
    bool IsActive,
    long? CreatedAt,
    string ReplacementPlan = "");

public sealed record RuleCheckGame(
    long GameId,
    bool Win,
    string ChampionName,
    long Timestamp);

public sealed record DerivedEventDefinitionRecord(
    long Id,
    string Name,
    IReadOnlyList<string> SourceTypes,
    int MinCount,
    int WindowSeconds,
    string Color,
    bool IsDefault,
    long? CreatedAt);

public sealed record ObjectiveGameEntry(
    long GameId,
    long ObjectiveId,
    bool Practiced,
    string ExecutionNote,
    string ChampionName,
    bool Win,
    long Timestamp,
    double Kills,
    double Deaths,
    double Assists,
    double KdaRatio,
    string ReviewNotes,
    bool HasReview);

// v2.15.0: free-form per-objective prompts. See docs/OBJECTIVES_CUSTOM_PROMPTS_PLAN.md.
public sealed record ObjectivePrompt(
    long Id,
    long ObjectiveId,
    string Phase,
    string Label,
    int SortOrder,
    long? CreatedAt);

/// <summary>
/// Row returned by <see cref="IPromptsRepository.GetActivePromptsForPhaseAsync"/>.
/// Contains everything the pre/post-game UI needs to render + persist an answer.
/// </summary>
public sealed record ActivePrompt(
    long PromptId,
    long ObjectiveId,
    string ObjectiveTitle,
    bool IsPriority,
    string Phase,
    string Label,
    int SortOrder);

/// <summary>
/// A prompt + its answer for a specific game. Used when loading a past
/// game's review so the UI can redisplay what the user answered.
/// </summary>
public sealed record PromptAnswer(
    long PromptId,
    long ObjectiveId,
    string ObjectiveTitle,
    bool IsPriority,
    string Phase,
    string Label,
    string AnswerText);

public sealed record DerivedEventInstanceRecord(
    long Id,
    long GameId,
    long DefinitionId,
    int StartTimeSeconds,
    int EndTimeSeconds,
    int EventCount,
    IReadOnlyList<long> SourceEventIds,
    string DefinitionName,
    string Color,
    IReadOnlyList<string> SourceTypes);

public sealed record EvidenceItemRecord(
    long Id,
    long GameId,
    string SourceKind,
    long? SourceId,
    string SourceKey,
    int? StartTimeSeconds,
    int? EndTimeSeconds,
    string Title,
    string Note,
    long? ObjectiveId,
    string ObjectiveTitle,
    long? ConceptTagId,
    string ConceptTagName,
    long? MatchupNoteId,
    string Polarity,
    string Status,
    long? CreatedAt,
    long? UpdatedAt,
    string ChampionName,
    bool? Win,
    long? GameTimestamp);

public sealed record EvidenceUpsert(
    long GameId,
    string SourceKind,
    long? SourceId,
    string SourceKey,
    int? StartTimeSeconds,
    int? EndTimeSeconds,
    string Title,
    string Note = "",
    long? ObjectiveId = null,
    long? ConceptTagId = null,
    long? MatchupNoteId = null,
    string Polarity = EvidencePolarities.Neutral,
    string Status = EvidenceStatuses.NeedsReview);

public sealed record ObjectivePatternCard(
    string Kind,
    string Title,
    string Detail,
    long? GameId = null,
    long? ObjectiveId = null,
    string Severity = "medium")
{
    /// <summary>
    /// Stable identity of this pattern for review-tracking. Kind alone for
    /// game/global patterns; kind + objective id for objective-scoped ones so
    /// two objectives with the same kind don't collide.
    /// </summary>
    public string PatternKey =>
        ObjectiveId is long oid ? $"{Kind}:obj{oid}" : Kind;
}

/// <summary>
/// One moment composing a cross-game pattern — an evidence item, joined to its
/// game's champion/result and (when available) its matched VOD path. Ordered
/// oldest-first so the Pattern Review viewer walks them chronologically.
/// </summary>
public sealed record PatternMoment(
    long EvidenceId,
    long GameId,
    string ChampionName,
    bool Win,
    long GameTimestamp,
    int? StartTimeSeconds,
    int? EndTimeSeconds,
    string Title,
    string Note,
    string Polarity,
    string SourceKind,
    string VodPath);

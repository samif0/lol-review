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
    bool PracticePost = false);

public sealed record GameObjectiveRecord(
    long GameId,
    long ObjectiveId,
    bool Practiced,
    string ExecutionNote,
    string Title,
    string CompletionCriteria,
    string Type,
    bool IsPriority,
    string Phase);

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
    long? ObjectiveId);

public sealed record RuleRecord(
    long Id,
    string Name,
    string Description,
    string RuleType,
    string ConditionValue,
    bool IsActive,
    long? CreatedAt);

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

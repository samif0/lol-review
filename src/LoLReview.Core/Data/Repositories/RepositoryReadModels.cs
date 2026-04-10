#nullable enable

namespace LoLReview.Core.Data.Repositories;

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
    long? CompletedAt);

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
    long? CreatedAt);

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

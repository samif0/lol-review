#nullable enable

using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

public sealed record ReviewTagState(
    long Id,
    string Name,
    string Polarity,
    string ColorHex,
    bool IsSelected);

public sealed record ReviewObjectiveState(
    long ObjectiveId,
    string Title,
    string Criteria,
    bool IsPriority,
    bool Practiced,
    string ExecutionNote);

public sealed record ReviewMatchupHistoryItem(
    string Note,
    bool? Helpful,
    long? GameId,
    long? CreatedAt);

public sealed record SaveObjectivePracticeRequest(
    long ObjectiveId,
    bool Practiced,
    string ExecutionNote);

public sealed record ReviewSnapshot(
    int MentalRating,
    string WentWell,
    string Mistakes,
    string FocusNext,
    string ReviewNotes,
    string ImprovementNote,
    string Attribution,
    string MentalHandled,
    string SpottedProblems,
    string OutsideControl,
    string WithinControl,
    string PersonalContribution,
    string EnemyLaner,
    string MatchupNote,
    IReadOnlyList<long> SelectedTagIds,
    IReadOnlyList<SaveObjectivePracticeRequest> ObjectivePractices);

public sealed record ReviewScreenData(
    GameStats Game,
    bool RequireReviewNotes,
    bool HasVod,
    int BookmarkCount,
    ObjectiveSummary? PriorityObjective,
    IReadOnlyList<ReviewTagState> Tags,
    IReadOnlyList<ReviewObjectiveState> ObjectiveAssessments,
    IReadOnlyList<ReviewMatchupHistoryItem> MatchupHistory,
    ReviewSnapshot Snapshot);

public sealed record ReviewDraftRequest(
    long GameId,
    ReviewSnapshot Snapshot);

public sealed record SaveReviewRequest(
    long GameId,
    string ChampionName,
    bool Win,
    bool RequireReviewNotes,
    ReviewSnapshot Snapshot);

public sealed record ReviewSaveResult(
    bool Success,
    string ErrorMessage,
    string SavedEnemyLaner)
{
    public static ReviewSaveResult Ok(string savedEnemyLaner) => new(true, "", savedEnemyLaner);

    public static ReviewSaveResult Fail(string errorMessage) => new(false, errorMessage, "");
}

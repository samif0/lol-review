#nullable enable

using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Services;

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
    // NULL = "leave the persisted value unchanged" for the fields below. The
    // review form does NOT render these (improvement/mental-handled/the three
    // attribution texts/enemy-laner/matchup-note), so its save payload omits them
    // and they arrive null — which must NOT overwrite migrated/pre-existing data
    // (the save path used to coerce null→"" and blind-UPDATE, wiping it). Callers
    // that DO own these fields (the read snapshot + draft builders) still pass a
    // concrete string. See ReviewWorkflowService.SaveAsync for the skip-on-null.
    string? ImprovementNote,
    string Attribution,
    string? MentalHandled,
    string SpottedProblems,
    string? OutsideControl,
    string? WithinControl,
    string? PersonalContribution,
    string? EnemyLaner,
    string? MatchupNote,
    IReadOnlyList<long> SelectedTagIds,
    IReadOnlyList<SaveObjectivePracticeRequest> ObjectivePractices,
    // v2.18 (schema v5): one-tap focus adherence. null = unanswered,
    // 0 = no, 1 = partly, 2 = yes.
    int? FocusAdherence = null);

public sealed record ReviewDraftRequest(
    long GameId,
    ReviewSnapshot Snapshot);

public sealed record SaveReviewRequest(
    long GameId,
    string ChampionName,
    bool Win,
    bool RequireReviewNotes,
    ReviewSnapshot Snapshot);

public sealed record VodCheckResult(bool HasVod, int BookmarkCount);

public sealed record ReviewSaveResult(
    bool Success,
    string ErrorMessage,
    string SavedEnemyLaner)
{
    public static ReviewSaveResult Ok(string savedEnemyLaner) => new(true, "", savedEnemyLaner);

    public static ReviewSaveResult Fail(string errorMessage) => new(false, errorMessage, "");
}

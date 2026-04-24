#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>Progression level info for an objective.</summary>
public sealed record ObjectiveLevelInfo(
    string LevelName,
    int LevelIndex,
    int Score,
    int GameCount,
    double Progress,
    int? NextThreshold,
    bool CanComplete,
    bool SuggestComplete);

/// <summary>CRUD + scoring for the objectives and game_objectives tables.</summary>
public interface IObjectivesRepository
{
    Task<long> CreateAsync(string title, string skillArea = "", string type = "primary",
        string completionCriteria = "", string description = "", string phase = ObjectivePhases.InGame);

    /// <summary>
    /// v2.15.0 multi-phase create. At least one of practicePre/In/Post must be true.
    /// The legacy single <c>phase</c> column gets set to whichever of the three is
    /// checked first (pre→in→post) for backwards-compatibility with callers that
    /// still read it.
    /// </summary>
    Task<long> CreateWithPhasesAsync(string title, string skillArea, string type,
        string completionCriteria, string description,
        bool practicePre, bool practiceIn, bool practicePost);

    Task<IReadOnlyList<ObjectiveSummary>> GetAllAsync();

    Task<IReadOnlyList<ObjectiveSummary>> GetActiveAsync();

    /// <summary>
    /// v2.15.0: return all active objectives whose <c>practice_&lt;phase&gt;</c>
    /// bool is set. <paramref name="phase"/> is "pregame" / "ingame" / "postgame".
    ///
    /// When <paramref name="championName"/> is non-null, filters out objectives
    /// that have an entry in <c>objective_champions</c> that doesn't match the
    /// given champion. Objectives with zero champion rows apply to everyone and
    /// always pass. When <paramref name="championName"/> is null, champion
    /// filtering is skipped entirely.
    /// </summary>
    Task<IReadOnlyList<ObjectiveSummary>> GetActiveByPhaseAsync(string phase, string? championName = null);

    // ── v2.15.0 champion gating ─────────────────────────────────────

    /// <summary>List champions the objective is scoped to. Empty list = all champions.</summary>
    Task<IReadOnlyList<string>> GetChampionsForObjectiveAsync(long objectiveId);

    /// <summary>
    /// Replace the champion set for an objective with the provided list. Pass
    /// an empty list to clear (objective applies to all champions). Diff-save
    /// under the hood so we don't churn rows that are already present.
    /// </summary>
    Task SetChampionsForObjectiveAsync(long objectiveId, IReadOnlyList<string> champions);

    /// <summary>
    /// Distinct champion names from the user's captured games, newest first.
    /// Used to prime the champion picker UI with champs the user actually plays.
    /// </summary>
    Task<IReadOnlyList<string>> GetPlayedChampionsAsync(int limit = 30);

    Task<ObjectiveSummary?> GetPriorityAsync();

    Task<ObjectiveSummary?> GetAsync(long objectiveId);

    Task SetPriorityAsync(long objectiveId);

    Task UpdateScoreAsync(long objectiveId, bool win);

    Task MarkCompleteAsync(long objectiveId);

    Task UpdateAsync(long objectiveId, string title, string skillArea = "", string type = "primary",
        string completionCriteria = "", string description = "", string phase = ObjectivePhases.InGame);

    /// <summary>v2.15.0 multi-phase update. Mirrors <see cref="CreateWithPhasesAsync"/>.</summary>
    Task UpdateWithPhasesAsync(long objectiveId, string title, string skillArea, string type,
        string completionCriteria, string description,
        bool practicePre, bool practiceIn, bool practicePost);

    Task UpdatePhaseAsync(long objectiveId, string phase);

    /// <summary>v2.15.0: update just the three practice bools without touching title/desc/etc.</summary>
    Task UpdatePracticePhasesAsync(long objectiveId, bool practicePre, bool practiceIn, bool practicePost);

    Task DeleteAsync(long objectiveId);

    Task RecordGameAsync(long gameId, long objectiveId, bool practiced, string executionNote = "");

    Task<IReadOnlyList<GameObjectiveRecord>> GetGameObjectivesAsync(long gameId);

    Task<IReadOnlyList<ObjectiveGameEntry>> GetGamesForObjectiveAsync(long objectiveId);

    /// <summary>
    /// Return cumulative score values per game, oldest first, capped at the last <paramref name="limit"/> games.
    /// Used to render the sparkline trend on the objective card.
    /// </summary>
    Task<IReadOnlyList<int>> GetScoreHistoryAsync(long objectiveId, int limit = 20);

    /// <summary>Return progression display info for a score + game count.</summary>
    static ObjectiveLevelInfo GetLevelInfo(int score, int gameCount)
    {
        ReadOnlySpan<(int Threshold, string Name)> levels =
        [
            (0, "Exploring"),
            (15, "Drilling"),
            (30, "Ingraining"),
            (50, "Ready"),
        ];

        int levelIdx = 0;
        for (int i = 0; i < levels.Length; i++)
        {
            if (score >= levels[i].Threshold)
                levelIdx = i;
        }

        string levelName = levels[levelIdx].Name;

        int? nextThreshold = levelIdx + 1 < levels.Length
            ? levels[levelIdx + 1].Threshold
            : null;

        // Overall progress across the whole 0 -> "Ready" arc, not per-stage.
        // Score of 50 (the highest named threshold) = 100%. This lets the UI
        // render a single continuous ring that fills as the user advances
        // through all stages, with the accent color shifting at each
        // boundary via LevelIndex.
        const int readyThreshold = 50;
        double progress = Math.Clamp((double)score / readyThreshold, 0.0, 1.0);

        return new ObjectiveLevelInfo(
            LevelName: levelName,
            LevelIndex: levelIdx,
            Score: score,
            GameCount: gameCount,
            Progress: progress,
            NextThreshold: nextThreshold,
            CanComplete: score >= readyThreshold,
            SuggestComplete: score >= readyThreshold);
    }
}

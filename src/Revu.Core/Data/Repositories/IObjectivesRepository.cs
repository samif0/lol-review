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

    Task<IReadOnlyList<ObjectiveSummary>> GetAllAsync();

    Task<IReadOnlyList<ObjectiveSummary>> GetActiveAsync();

    Task<ObjectiveSummary?> GetPriorityAsync();

    Task<ObjectiveSummary?> GetAsync(long objectiveId);

    Task SetPriorityAsync(long objectiveId);

    Task UpdateScoreAsync(long objectiveId, bool win);

    Task MarkCompleteAsync(long objectiveId);

    Task UpdateAsync(long objectiveId, string title, string skillArea = "", string type = "primary",
        string completionCriteria = "", string description = "", string phase = ObjectivePhases.InGame);

    Task UpdatePhaseAsync(long objectiveId, string phase);

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

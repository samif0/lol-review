#nullable enable

namespace LoLReview.Core.Data.Repositories;

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
        string completionCriteria = "", string description = "");

    Task<IReadOnlyList<ObjectiveSummary>> GetAllAsync();

    Task<IReadOnlyList<ObjectiveSummary>> GetActiveAsync();

    Task<ObjectiveSummary?> GetPriorityAsync();

    Task<ObjectiveSummary?> GetAsync(long objectiveId);

    Task SetPriorityAsync(long objectiveId);

    Task UpdateScoreAsync(long objectiveId, bool win);

    Task MarkCompleteAsync(long objectiveId);

    Task DeleteAsync(long objectiveId);

    Task RecordGameAsync(long gameId, long objectiveId, bool practiced, string executionNote = "");

    Task<IReadOnlyList<GameObjectiveRecord>> GetGameObjectivesAsync(long gameId);

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
        int levelStart = levels[levelIdx].Threshold;

        int? nextThreshold;
        double progress;
        if (levelIdx + 1 < levels.Length)
        {
            nextThreshold = levels[levelIdx + 1].Threshold;
            progress = Math.Min(1.0, (double)(score - levelStart) / (nextThreshold.Value - levelStart));
        }
        else
        {
            nextThreshold = null;
            progress = 1.0;
        }

        return new ObjectiveLevelInfo(
            LevelName: levelName,
            LevelIndex: levelIdx,
            Score: score,
            GameCount: gameCount,
            Progress: progress,
            NextThreshold: nextThreshold,
            CanComplete: gameCount >= 30,
            SuggestComplete: score >= 50 && gameCount >= 30);
    }
}

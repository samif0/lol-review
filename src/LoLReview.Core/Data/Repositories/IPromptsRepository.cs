#nullable enable

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for objective_prompts and prompt_answers tables.</summary>
public interface IPromptsRepository
{
    Task<long> CreatePromptAsync(long objectiveId, string questionText,
        string eventTag = "", string answerType = "yes_no", int sortOrder = 0);

    Task<IReadOnlyList<Dictionary<string, object?>>> GetPromptsForObjectiveAsync(long objectiveId);

    Task DeletePromptsForObjectiveAsync(long objectiveId);

    Task SaveAnswerAsync(long gameId, long promptId, int answerValue,
        long? eventInstanceId = null, int? eventTimeSeconds = null);

    Task<IReadOnlyList<Dictionary<string, object?>>> GetAnswersForGameAsync(long gameId);

    /// <summary>Get answer scores over time for an objective's prompts.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetProgressionDataAsync(long objectiveId);
}

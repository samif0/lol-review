#nullable enable

namespace LoLReview.Core.Data.Repositories;

/// <summary>
/// Persists user decisions for recently missed-game ingestion prompts.
/// </summary>
public interface IMissedGameDecisionRepository
{
    /// <summary>Return the subset of supplied game ids that were previously dismissed.</summary>
    Task<HashSet<long>> GetDismissedGameIdsAsync(IEnumerable<long> gameIds);

    /// <summary>Persist a durable dismissal for the supplied game ids.</summary>
    Task MarkDismissedAsync(IEnumerable<long> gameIds);
}

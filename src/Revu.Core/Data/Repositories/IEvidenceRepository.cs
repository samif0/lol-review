#nullable enable

namespace Revu.Core.Data.Repositories;

public interface IEvidenceRepository
{
    Task<long> UpsertAsync(EvidenceUpsert item);

    Task<IReadOnlyList<EvidenceItemRecord>> GetForGameAsync(long gameId, bool includeDismissed = false);

    Task<IReadOnlyList<EvidenceItemRecord>> GetForObjectiveAsync(long objectiveId, bool includeDismissed = false);

    Task<IReadOnlyList<EvidenceItemRecord>> GetRecentAsync(int limit = 20, bool includeDismissed = false);

    Task<int> CountPendingAsync();

    Task UpdateStatusAsync(long evidenceId, string status);

    /// <summary>Permanently remove an evidence row by id (hard delete).</summary>
    Task DeleteAsync(long evidenceId);

    Task UpdatePolarityAsync(long evidenceId, string polarity);

    Task UpdateObjectiveAsync(long evidenceId, long? objectiveId);

    Task UpdateNoteAsync(long evidenceId, string note);

    Task<IReadOnlyList<ObjectivePatternCard>> GetPatternCardsAsync(int limit = 6);
}

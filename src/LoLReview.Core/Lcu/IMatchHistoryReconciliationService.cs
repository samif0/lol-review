#nullable enable

using LoLReview.Core.Services;

namespace LoLReview.Core.Lcu;

public interface IMatchHistoryReconciliationService
{
    Task<IReadOnlyList<MissedGameCandidate>> FindMissedGamesAsync(
        Func<long, bool>? checkGameSaved,
        CancellationToken cancellationToken = default);
}

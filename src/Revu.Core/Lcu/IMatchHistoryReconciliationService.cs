#nullable enable

using Revu.Core.Services;

namespace Revu.Core.Lcu;

public interface IMatchHistoryReconciliationService
{
    Task<IReadOnlyList<MissedGameCandidate>> FindMissedGamesAsync(
        Func<long, bool>? checkGameSaved,
        CancellationToken cancellationToken = default);
}

#nullable enable

namespace LoLReview.Core.Services;

public interface IReviewWorkflowService
{
    Task<ReviewScreenData?> LoadAsync(long gameId, CancellationToken cancellationToken = default);

    Task<ReviewSaveResult> SaveAsync(SaveReviewRequest request, CancellationToken cancellationToken = default);

    Task<bool> SaveDraftAsync(ReviewDraftRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewMatchupHistoryItem>> GetMatchupHistoryAsync(
        string championName,
        string enemyLaner,
        long currentGameId,
        CancellationToken cancellationToken = default);
}

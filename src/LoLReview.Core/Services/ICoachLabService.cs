#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

public interface ICoachLabService
{
    bool IsEnabled { get; }

    Task<CoachDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CoachMomentCard>> GetMomentQueueAsync(int limit = 50, CancellationToken cancellationToken = default);

    Task<CoachMomentCard?> GetMomentAsync(long momentId, CancellationToken cancellationToken = default);

    Task<CoachSyncResult> SyncMomentsAsync(bool includeAutoSamples = true, CancellationToken cancellationToken = default);

    Task<int> RedraftPendingMomentsAsync(CancellationToken cancellationToken = default);

    Task<int> RedraftAllMomentsAsync(CancellationToken cancellationToken = default);

    Task SaveManualLabelAsync(long momentId, CoachManualLabelInput input, CancellationToken cancellationToken = default);

    Task<CoachRecommendation?> RefreshRecommendationAsync(CancellationToken cancellationToken = default);

    Task<CoachProblemsReport> GetModelProblemsAsync(CancellationToken cancellationToken = default);

    Task<CoachObjectiveSuggestion> GenerateObjectiveSuggestionAsync(CancellationToken cancellationToken = default);
}

#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

public interface ICoachRecommendationService
{
    Task<CoachRecommendation> BuildAssistRecommendationAsync(long playerId, CoachObjectiveBlock block, CancellationToken cancellationToken = default);
    Task<CoachProblemsReport> BuildProblemsReportAsync(long playerId, CoachObjectiveBlock block, CancellationToken cancellationToken = default);
    Task<CoachObjectiveSuggestion> GenerateObjectiveSuggestionAsync(long playerId, CoachObjectiveBlock block, CancellationToken cancellationToken = default);
}

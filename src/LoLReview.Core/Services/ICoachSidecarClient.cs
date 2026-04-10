#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

public interface ICoachSidecarClient
{
    Task<CoachDraftResult> DraftMomentAsync(CoachDraftRequest request, CancellationToken cancellationToken = default);
    Task<CoachProblemsReport> AnalyzeProblemsAsync(CoachProblemAnalysisRequest request, CancellationToken cancellationToken = default);
    Task<CoachObjectiveSuggestion> PlanObjectiveAsync(CoachObjectivePlanRequest request, CancellationToken cancellationToken = default);
}

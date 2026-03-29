#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

public interface ICoachSidecarClient
{
    Task<CoachDraftResult> DraftMomentAsync(CoachDraftRequest request, CancellationToken cancellationToken = default);
}

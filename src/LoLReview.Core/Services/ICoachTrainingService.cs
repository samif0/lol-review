#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

public interface ICoachTrainingService
{
    Task<CoachTrainingStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<CoachTrainResult> TrainPrematureModelAsync(CancellationToken cancellationToken = default);
}

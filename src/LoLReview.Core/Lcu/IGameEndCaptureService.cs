#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Lcu;

public interface IGameEndCaptureService
{
    Task<GameStats?> CaptureAsync(
        IReadOnlyList<GameEvent> liveEvents,
        CancellationToken cancellationToken = default);
}

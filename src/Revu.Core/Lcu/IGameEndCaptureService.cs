#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Lcu;

public interface IGameEndCaptureService
{
    Task<GameStats?> CaptureAsync(
        IReadOnlyList<GameEvent> liveEvents,
        CancellationToken cancellationToken = default);
}

#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// On-demand batch clipping of objective-tied timeline events. Extracts a ~45s clip
/// (PreRoll before to PostRoll after, from <see cref="Revu.Core.Constants.GameConstants"/>)
/// around every event tied to the user's active learning objectives, reusing the
/// manual clip tool's ffmpeg + persistence path. Gated by
/// <see cref="IConfigService.AutoClipObjectivesEnabled"/>.
/// </summary>
public interface IAutoClipService
{
    /// <summary>
    /// Clip the objective-tied events for a game.
    /// </summary>
    /// <param name="gameId">The game to clip.</param>
    /// <param name="objectiveId">
    /// When set, only clip events tied to THIS objective (the framed view). Null clips
    /// every active-objective-tied event.
    /// </param>
    Task<AutoClipResult> ClipObjectiveEventsAsync(long gameId, long? objectiveId, CancellationToken ct = default);
}

/// <summary>Outcome of an auto-clip run, surfaced to the caller / UI.</summary>
/// <param name="Created">Clips successfully extracted + persisted.</param>
/// <param name="Skipped">Eligible events not clipped (already clipped, min-gap, or cap).</param>
/// <param name="Reason">
/// A short machine reason when nothing useful happened: "disabled" (toggle off),
/// "no_vod" (no playable recording on disk), "no_events" (no tied events / none new),
/// or "" on a normal run.
/// </param>
public readonly record struct AutoClipResult(int Created, int Skipped, string Reason);

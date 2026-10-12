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
    /// <param name="progress">Reported before each planned clip, with <c>Done</c> = the planned clips
    /// finished so far (extracted or skipped). There is no final report: the returned result is it.</param>
    Task<AutoClipResult> ClipObjectiveEventsAsync(long gameId, long? objectiveId, CancellationToken ct = default,
        IProgress<AutoClipProgress>? progress = null);
}

/// <summary>Where a running auto-clip batch is: <paramref name="Done"/> of <paramref name="Total"/> planned clips.</summary>
public readonly record struct AutoClipProgress(int Done, int Total, int Created, int Skipped);

/// <summary>Outcome of an auto-clip run, surfaced to the caller / UI.</summary>
/// <param name="Created">Clips successfully extracted + persisted.</param>
/// <param name="Skipped">Eligible events not clipped (already clipped, min-gap, or cap).</param>
/// <param name="Reason">
/// A short machine reason when nothing useful happened: "disabled" (toggle off),
/// "no_vod" (no playable recording on disk), "no_events" (no tied events / none new),
/// or "" on a normal run.
/// </param>
public readonly record struct AutoClipResult(int Created, int Skipped, string Reason);

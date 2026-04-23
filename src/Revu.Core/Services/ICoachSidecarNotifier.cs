#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Abstraction for notifying the coach sidecar about lifecycle events.
///
/// Core doesn't know about HTTP or the App-layer client. This interface
/// lets GameLifecycleWorkflowService / ReviewWorkflowService fire
/// best-effort messages at the sidecar without depending on the App
/// layer.
///
/// The App layer registers a real implementation; Core ships a no-op
/// fallback so missing App-side registration never breaks Core logic.
/// </summary>
public interface ICoachSidecarNotifier
{
    /// <summary>Called after a game finishes and is persisted. Fire-and-forget.</summary>
    Task NotifyGameEndedAsync(long gameId, CancellationToken cancellationToken = default);

    /// <summary>Called after a review is saved. Triggers concept extraction. Fire-and-forget.</summary>
    Task NotifyReviewSavedAsync(long gameId, CancellationToken cancellationToken = default);

    /// <summary>Called after a VOD bookmark is created. Triggers vision describe. Fire-and-forget.</summary>
    Task NotifyBookmarkCreatedAsync(long bookmarkId, CancellationToken cancellationToken = default);
}

/// <summary>Default no-op implementation. Used when coach App-layer is absent.</summary>
public sealed class NullCoachSidecarNotifier : ICoachSidecarNotifier
{
    public Task NotifyGameEndedAsync(long gameId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task NotifyReviewSavedAsync(long gameId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task NotifyBookmarkCreatedAsync(long bookmarkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

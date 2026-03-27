#nullable enable

using Microsoft.UI.Dispatching;

namespace LoLReview.App.Helpers;

/// <summary>
/// Static helper to marshal work onto the UI thread from background threads.
/// </summary>
public static class DispatcherHelper
{
    private static DispatcherQueue? _dispatcherQueue;

    /// <summary>
    /// Initialize the helper with the UI thread's DispatcherQueue.
    /// Must be called during app startup on the UI thread.
    /// </summary>
    public static void Initialize()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Run an action on the UI thread. If already on the UI thread, runs synchronously.
    /// </summary>
    public static void RunOnUIThread(Action action)
    {
        if (_dispatcherQueue is null)
        {
            // Fallback: just run directly (may fail if not on UI thread)
            action();
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
    }

    /// <summary>
    /// Run an action on the UI thread with normal priority.
    /// Returns false if the enqueue failed.
    /// </summary>
    public static bool TryRunOnUIThread(Action action)
    {
        if (_dispatcherQueue is null) return false;

        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
            return true;
        }

        return _dispatcherQueue.TryEnqueue(() => action());
    }
}

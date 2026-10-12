namespace Revu.Sidecar;

/// <summary>
/// Long requests accepted as background work, keyed by what they act on (one auto-clip
/// batch per game, one update download). The work runs on <see cref="SidecarBackgroundWork"/>
/// with its <see cref="SidecarBackgroundWork.Stopping"/> token, never the request's, so a
/// client that stops waiting (its IPC deadline) neither kills the work nor loses its result,
/// and shutdown still drains or cancels it. A request for a key that is already running joins
/// that run instead of starting a duplicate. Progress and completion go out over SSE from the
/// work itself; the request just awaits the shared task while its client is still listening.
/// </summary>
public sealed class SidecarJobs
{
    private readonly SidecarBackgroundWork _work;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);

    public SidecarJobs(SidecarBackgroundWork work) => _work = work;

    /// <summary>
    /// The task of the run already going for <paramref name="key"/>, or of a new run of
    /// <paramref name="run"/>. Null once shutdown has begun (nothing was started). A run
    /// cancelled by shutdown completes as cancelled; a run that throws completes faulted.
    /// </summary>
    public Task<T>? Start<T>(string key, string operation, Func<CancellationToken, Task<T>> run)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(key, out var existing)) return (Task<T>)existing;
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_work.TryRun(operation, async () =>
            {
                try { completion.TrySetResult(await run(_work.Stopping).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (_work.Stopping.IsCancellationRequested)
                {
                    completion.TrySetCanceled(_work.Stopping);
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                    throw; // SidecarBackgroundWork logs it
                }
                finally
                {
                    lock (_gate) _running.Remove(key);
                }
            })) return null;
            // Still under the lock, so the run's own removal cannot overtake this insert.
            _running[key] = completion.Task;
            return completion.Task;
        }
    }

    public bool IsRunning(string key)
    {
        lock (_gate) return _running.ContainsKey(key);
    }
}

/// <summary>Reports on the caller's thread, in order (Progress&lt;T&gt; posts asynchronously).</summary>
internal sealed class InlineJobProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

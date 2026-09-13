namespace Revu.Sidecar;

/// <summary>Tracks application writes that outlive an HTTP request or LCU message.</summary>
public sealed class SidecarBackgroundWork : IHostedService
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Task> _pending = new();
    private readonly ILogger<SidecarBackgroundWork> _logger;
    private readonly TimeSpan _shutdownTimeout;
    private readonly CancellationTokenSource _stoppingSignal = new();
    private long _nextId;
    private bool _stopping;

    public SidecarBackgroundWork(ILogger<SidecarBackgroundWork> logger, TimeSpan? shutdownTimeout = null)
    {
        _logger = logger;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(10);
    }

    public bool ShutdownIncomplete { get; private set; }
    // Only cancellable delays/retries use this signal; submitted game saves drain normally.
    public CancellationToken Stopping => _stoppingSignal.Token;
    public bool HasOutstandingWork { get { lock (_gate) return _pending.Count != 0; } }

    public bool TryRun(string operation, Func<Task> work)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_gate)
        {
            if (_stopping) return false;
            id = ++_nextId;
            _pending.Add(id, completion.Task);
        }
        _ = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (OperationCanceledException) when (Stopping.IsCancellationRequested) { }
            catch (Exception error) { _logger.LogError(error, "Background operation {Operation} failed", operation); }
            finally
            {
                lock (_gate) _pending.Remove(id);
                completion.TrySetResult();
            }
        });
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (_gate)
        {
            _stopping = true;
            pending = _pending.Values.ToArray();
        }
        _stoppingSignal.Cancel();
        try { await Task.WhenAll(pending).WaitAsync(_shutdownTimeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            ShutdownIncomplete = HasOutstandingWork;
            if (ShutdownIncomplete)
                _logger.LogCritical("Shutdown could not finish pending background work within {Seconds} seconds. " +
                    "The process will report failure; verify the last game's save before restarting.", _shutdownTimeout.TotalSeconds);
        }
    }
}

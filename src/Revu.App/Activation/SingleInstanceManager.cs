#nullable enable

namespace Revu.App.Activation;

/// <summary>
/// Ensures only one instance of LoL Review can run at a time using a named Mutex.
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "Revu_SingleInstance";
    private Mutex? _mutex;
    private bool _hasHandle;

    /// <summary>
    /// Attempt to acquire the single-instance mutex.
    /// Returns <c>true</c> if this is the first instance, <c>false</c> if another is already running.
    /// </summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(false, MutexName, out _);

        try
        {
            _hasHandle = _mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed — we now own the mutex
            _hasHandle = true;
        }

        return _hasHandle;
    }

    /// <summary>
    /// Release the mutex so another instance could start.
    /// </summary>
    public void Release()
    {
        if (_hasHandle && _mutex is not null)
        {
            _mutex.ReleaseMutex();
            _hasHandle = false;
        }
    }

    public void Dispose()
    {
        Release();
        _mutex?.Dispose();
        _mutex = null;
    }
}

#nullable enable

using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>Serializes async mutations that must preserve caller order.</summary>
public sealed class SerializedTaskQueue
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly string _name;
    private Task _tail = Task.CompletedTask;

    public SerializedTaskQueue(ILogger logger, string name)
    {
        _logger = logger;
        _name = name;
    }

    public Task EnqueueAsync(Func<Task> operation)
    {
        lock (_gate)
        {
            var previous = _tail;
            var next = Task.Run(() => RunAfterPreviousAsync(previous, operation));
            _tail = next;
            return next;
        }
    }

    public Task<T> EnqueueAsync<T>(Func<Task<T>> operation)
    {
        lock (_gate)
        {
            var previous = _tail;
            var next = Task.Run(() => RunAfterPreviousAsync(previous, operation));
            _tail = next;
            return next;
        }
    }

    private async Task RunAfterPreviousAsync(Task previous, Func<Task> operation)
    {
        await ObservePreviousAsync(previous).ConfigureAwait(false);
        await operation().ConfigureAwait(false);
    }

    private async Task<T> RunAfterPreviousAsync<T>(Task previous, Func<Task<T>> operation)
    {
        await ObservePreviousAsync(previous).ConfigureAwait(false);
        return await operation().ConfigureAwait(false);
    }

    private async Task ObservePreviousAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Previous {QueueName} operation failed", _name);
        }
    }
}

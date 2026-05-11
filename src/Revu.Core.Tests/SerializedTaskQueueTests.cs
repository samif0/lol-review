using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

public sealed class SerializedTaskQueueTests
{
    [Fact]
    public async Task EnqueueAsync_RunsOperationsInSubmissionOrder()
    {
        var queue = new SerializedTaskQueue(NullLogger.Instance, "test");
        var order = new List<int>();

        var first = queue.EnqueueAsync(async () =>
        {
            await Task.Delay(20);
            order.Add(1);
        });
        var second = queue.EnqueueAsync(() =>
        {
            order.Add(2);
            return Task.CompletedTask;
        });

        await Task.WhenAll(first, second);

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task EnqueueAsync_ContinuesAfterPriorFailure()
    {
        var queue = new SerializedTaskQueue(NullLogger.Instance, "test");
        var completed = false;

        var failed = queue.EnqueueAsync(() => throw new InvalidOperationException("boom"));
        var next = queue.EnqueueAsync(() =>
        {
            completed = true;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        await next;

        Assert.True(completed);
    }
}

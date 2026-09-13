using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class SidecarBackgroundWorkTests
{
    [Fact]
    public async Task ShutdownStopsAdmissionAndWaitsForAnAcceptedSave()
    {
        var work = new SidecarBackgroundWork(NullLogger<SidecarBackgroundWork>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = false;
        Assert.True(work.TryRun("save", async () => { started.SetResult(); await finish.Task; saved = true; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopping = work.StopAsync(CancellationToken.None);
        Assert.False(work.TryRun("late save", () => throw new InvalidOperationException("must not run")));
        Assert.False(stopping.IsCompleted);
        Assert.False(saved);
        finish.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(saved);
        Assert.False(work.HasOutstandingWork);
        Assert.False(work.ShutdownIncomplete);
    }

    [Fact]
    public async Task ABackgroundFailureIsObservedAndDoesNotPreventTheOtherSaveDraining()
    {
        var logger = new RecordingLogger();
        var work = new SidecarBackgroundWork(logger);
        Assert.True(work.TryRun("failed save", () => throw new InvalidOperationException("fixture failure")));
        var saved = false;
        Assert.True(work.TryRun("successful save", () => { saved = true; return Task.CompletedTask; }));
        await work.StopAsync(CancellationToken.None);
        Assert.True(saved);
        Assert.Single(logger.Levels, level => level == LogLevel.Error);
        Assert.False(work.HasOutstandingWork);
    }

    [Fact]
    public async Task PendingRetryDelayCancelsWithoutCancellingSubmittedSaves()
    {
        var logger = new RecordingLogger();
        var work = new SidecarBackgroundWork(logger);
        Assert.True(work.TryRun("retry delay", () => Task.Delay(Timeout.InfiniteTimeSpan, work.Stopping)));
        await work.StopAsync(CancellationToken.None);
        Assert.False(work.HasOutstandingWork);
        Assert.False(work.ShutdownIncomplete);
        Assert.DoesNotContain(LogLevel.Error, logger.Levels);
    }

    [Fact]
    public async Task TimeoutReportsIncompleteWorkWithoutPretendingItFinished()
    {
        var logger = new RecordingLogger();
        var work = new SidecarBackgroundWork(logger, TimeSpan.FromMilliseconds(20));
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(work.TryRun("blocked save", () => finish.Task));
        try
        {
            await work.StopAsync(CancellationToken.None);
            Assert.True(work.ShutdownIncomplete);
            Assert.True(work.HasOutstandingWork);
            Assert.Contains(LogLevel.Critical, logger.Levels);
        }
        finally
        {
            finish.TrySetResult();
            await work.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingLogger : ILogger<SidecarBackgroundWork>
    {
        public ConcurrentQueue<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Levels.Enqueue(logLevel);
    }
}

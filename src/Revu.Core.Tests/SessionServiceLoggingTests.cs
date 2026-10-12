using Microsoft.Extensions.Logging;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The sidecar now writes Information logs to sidecar.log, which users can attach to
/// support requests. A session intention is personal free text, so the log line says
/// that one was set and never what it says.
/// </summary>
public sealed class SessionServiceLoggingTests
{
    [Fact]
    public async Task SetIntention_SavesTheTextButLogsOnlyTheDate()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var logger = new CapturingLogger();
        var service = new SessionService(scope.SessionLog, logger);
        const string intention = "Stop flaming my jungler and ward river before 3:15";

        await service.SetIntentionAsync("2026-10-12", intention);

        Assert.Equal(intention, (await scope.SessionLog.GetSessionAsync("2026-10-12"))?.Intention);
        var line = Assert.Single(logger.Messages);
        Assert.Contains("2026-10-12", line);
        Assert.DoesNotContain("jungler", line);
    }

    private sealed class CapturingLogger : ILogger<SessionService>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

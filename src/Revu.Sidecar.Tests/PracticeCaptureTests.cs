using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services.EventProcessing;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class PracticeCaptureTests
{
    [Fact]
    public async Task PrefixReplay_PreservesBeforeGapFacts_AndDoesNotBridgeMissingData()
    {
        var path = Path.GetTempFileName();
        string Sample(int sequence, int hp) => JsonSerializer.Serialize(new { version = 1, source = "riot.live-client",
            receivedUtc = DateTimeOffset.UtcNow, requestMilliseconds = 2,
            payload = new { gameData = new { gameTime = sequence }, activePlayer = new { championStats = new { currentHealth = hp, maxHealth = 600 } } } });
        try
        {
            await File.WriteAllLinesAsync(path, [Sample(1,600), Sample(2,500), "{\"gap\":true}", Sample(3,400)]);
            var replay = await PracticeCaptureService.ReplayCapturedPrefixAsync(path, new StoppedClock());
            Assert.True(replay.EndedAtGap);
            Assert.Equal(2, replay.Samples);
            Assert.Equal(2, replay.LastGameSeconds);
            Assert.Single(replay.Report.Events);
            Assert.Empty(EventEligibility.ForConsumers(replay.Report.Events));
        }
        finally { File.Delete(path); }
    }

    private sealed class Handler(bool available = true) : HttpMessageHandler
    {
        private int _sample;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://127.0.0.1:2999/liveclientdata/allgamedata", request.RequestUri!.ToString());
            if (!available) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            int sample = Interlocked.Increment(ref _sample);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { gameData = new { gameTime = sample },
                    activePlayer = new { championStats = new { currentHealth = 600 - sample * 10, maxHealth = 600 } } })) });
        }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) { Assert.Equal("LiveEventApi", name); return client; }
    }

    /// <summary>
    /// Polling time that stands still: no request takes any time, so every sample's clock
    /// uncertainty is zero, and the capture loop's wait before its next request ends only
    /// when the test ends it.
    /// </summary>
    private sealed class PollingClock : TimeProvider
    {
        private readonly SemaphoreSlim _waits = new(0);
        private Action? _endWait;

        public override long GetTimestamp() => 0;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Volatile.Write(ref _endWait, () => callback(state));
            _waits.Release();
            return new Wait();
        }

        /// <summary>Returns once the loop is waiting before its next request.</summary>
        public async Task WaitingAsync() =>
            Assert.True(await _waits.WaitAsync(TimeSpan.FromSeconds(30)), "The capture loop never waited for its next request");

        /// <summary>Ends that wait on a pool thread, as a real timer would.</summary>
        public void EndWait()
        {
            var end = Volatile.Read(ref _endWait)!;
            ThreadPool.QueueUserWorkItem(_ => end());
        }

        private sealed class Wait : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ArmIsIdempotent_CapturesEvidence_AndNeverProducesEligibleTrades()
    {
        using var client = new HttpClient(new Handler());
        var polling = new PollingClock();
        using var service = new PracticeCaptureService(new Factory(client), NullLogger<PracticeCaptureService>.Instance,
            processingTime: new StoppedClock(), pollingTime: polling);
        var status = service.Arm();
        Assert.Equal(status.Directory, service.Arm().Directory);
        try
        {
            // The first request goes out at once and the next only after the test ends the
            // loop's wait, so the capture holds exactly two samples when it stops.
            await polling.WaitingAsync();
            Assert.Equal(1, service.Status.Samples);
            polling.EndWait();
            await polling.WaitingAsync();
            var stopped = await service.StopAsync();
            Assert.Equal(2, stopped.Samples);
            Assert.Equal("stopped", stopped.State);
            var report = JsonSerializer.Deserialize<ProcessingReport>(await File.ReadAllTextAsync(Path.Combine(status.Directory,"processing-report.json")))!;
            var change = Assert.Single(report.Events);
            Assert.Empty(EventEligibility.ForConsumers(report.Events));
            Assert.Equal("DIAGNOSTIC_HEALTH_CHANGE", change.EventType);
            using var evidence = JsonDocument.Parse(change.Details);
            var observations = evidence.RootElement.GetProperty("processing").GetProperty("evidence");
            Assert.Equal(2, observations.GetArrayLength());
            Assert.All(observations.EnumerateArray(), o => Assert.Equal(0d, o.GetProperty("clockUncertaintyMs").GetDouble()));
            Assert.True(File.Exists(Path.Combine(status.Directory,"observations.jsonl")));
        }
        finally { await service.StopAsync(); Directory.Delete(status.Directory, true); }
    }

    [Fact]
    public async Task UnavailableSourceWaits_AndStopPersistsEmptyReport()
    {
        using var client = new HttpClient(new Handler(false));
        using var service = new PracticeCaptureService(new Factory(client), NullLogger<PracticeCaptureService>.Instance);
        var status = service.Arm();
        try
        {
            var stopped = await service.StopAsync();
            Assert.Equal(0, stopped.Samples);
            var report = JsonSerializer.Deserialize<ProcessingReport>(await File.ReadAllTextAsync(Path.Combine(status.Directory,"processing-report.json")))!;
            Assert.Empty(report.Events);
        }
        finally { await service.StopAsync(); Directory.Delete(status.Directory, true); }
    }
}

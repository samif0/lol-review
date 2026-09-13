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
            var replay = await PracticeCaptureService.ReplayCapturedPrefixAsync(path);
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

    [Fact]
    public async Task ArmIsIdempotent_CapturesEvidence_AndNeverProducesEligibleTrades()
    {
        using var client = new HttpClient(new Handler());
        using var service = new PracticeCaptureService(new Factory(client), NullLogger<PracticeCaptureService>.Instance);
        var status = service.Arm();
        Assert.Equal(status.Directory, service.Arm().Directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (service.Status.Samples < 2) await Task.Delay(25, timeout.Token);
            var stopped = await service.StopAsync();
            Assert.True(stopped.Samples >= 2);
            Assert.Equal("stopped", stopped.State);
            var report = JsonSerializer.Deserialize<ProcessingReport>(await File.ReadAllTextAsync(Path.Combine(status.Directory,"processing-report.json")))!;
            Assert.NotEmpty(report.Events);
            Assert.Empty(EventEligibility.ForConsumers(report.Events));
            Assert.All(report.Events, e=>Assert.Equal("DIAGNOSTIC_HEALTH_CHANGE", e.EventType));
            using var evidence = JsonDocument.Parse(report.Events[0].Details);
            Assert.Equal(2, evidence.RootElement.GetProperty("processing").GetProperty("evidence").GetArrayLength());
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

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// v3.2: the Riot key's sustained budget (~100 calls / 2 min) makes 429s a
/// NORMAL part of any long backfill. The client must honor Retry-After and
/// retry the same request instead of burning the game as failed — and still
/// give up (null) when the window never opens.
/// </summary>
public sealed class RiotMatchClientRateLimitTests
{
    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public int Sent { get; private set; }

        public QueuedHandler(params Func<HttpResponseMessage>[] responses) =>
            _responses = new Queue<Func<HttpResponseMessage>>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            var make = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(make());
        }
    }

    private static HttpResponseMessage RateLimited()
    {
        var res = new HttpResponseMessage((HttpStatusCode)429);
        // Zero-delay Retry-After keeps the retry loop instant under test.
        res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        return res;
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static RiotMatchClient Client(QueuedHandler handler)
    {
        var config = new TestConfigService(new AppConfig
        {
            RiotSessionToken = "test-token",
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        });
        return new RiotMatchClient(new HttpClient(handler), config, NullLogger<RiotMatchClient>.Instance);
    }

    [Fact]
    public async Task RetriesThrough429_ThenReturnsPayload()
    {
        var handler = new QueuedHandler(
            RateLimited,
            RateLimited,
            () => Ok("{\"info\":{\"gameId\":42}}"));
        var client = Client(handler);

        var doc = await client.GetMatchAsync("NA1_42", "na1");

        Assert.NotNull(doc);
        Assert.Equal(42, doc!.Value.GetProperty("info").GetProperty("gameId").GetInt32());
        Assert.Equal(3, handler.Sent); // two 429s absorbed, third attempt succeeded
    }

    [Fact]
    public async Task GivesUp_WhenRateLimitNeverLifts()
    {
        var handler = new QueuedHandler(RateLimited);
        var client = Client(handler);

        var doc = await client.GetTimelineAsync("NA1_42", "na1");

        Assert.Null(doc);
        Assert.Equal(4, handler.Sent); // initial + MaxRateLimitRetries, then null
    }

    // Never answers; only cancellation ends the send (HttpClient's timeout or the caller's).
    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static RiotMatchClient Client(HttpMessageHandler handler, TimeSpan timeout)
    {
        var config = new TestConfigService(new AppConfig
        {
            RiotSessionToken = "test-token",
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        });
        return new RiotMatchClient(new HttpClient(handler) { Timeout = timeout }, config, NullLogger<RiotMatchClient>.Instance);
    }

    [Fact]
    public async Task HttpClientTimeout_ReturnsNull_PerTheContract_InsteadOfAbortingTheCaller()
    {
        // One slow proxy response used to surface as a cancellation and end a whole
        // backfill run; it is a failed lookup like any other.
        var handler = new HangingHandler();
        var client = Client(handler, TimeSpan.FromMilliseconds(50));

        Assert.Null(await client.GetMatchAsync("NA1_42", "na1"));
        Assert.Null(await client.GetTimelineAsync("NA1_42", "na1"));
        Assert.Equal(2, handler.Sent);
    }

    [Fact]
    public async Task CallerCancellation_StillPropagates()
    {
        var client = Client(new HangingHandler(), Timeout.InfiniteTimeSpan);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMatchAsync("NA1_42", "na1", cts.Token));
    }

    [Fact]
    public async Task NonRetryableFailure_ReturnsNullWithoutRetry()
    {
        var handler = new QueuedHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = Client(handler);

        var doc = await client.GetMatchAsync("NA1_42", "na1");

        Assert.Null(doc);
        Assert.Equal(1, handler.Sent);
    }
}

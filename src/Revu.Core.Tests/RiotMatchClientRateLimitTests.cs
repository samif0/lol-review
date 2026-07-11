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

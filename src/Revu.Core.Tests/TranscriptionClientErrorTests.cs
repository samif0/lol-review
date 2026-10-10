using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// How a failed <c>POST /transcribe</c> is classified. A proxy older than 3.14 has no
/// route (404), which must read as "not available yet" (re-queued at the next start),
/// never as a permanent failure.
/// </summary>
public sealed class TranscriptionClientErrorTests
{
    private sealed class Stub(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(response);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static Task<TranscriptionChunkResult> Call(HttpResponseMessage response) =>
        new TranscriptionClient(new HttpClient(new Stub(response)), NullLogger<TranscriptionClient>.Instance, "https://proxy.test")
            .TranscribeChunkAsync([1, 2, 3], 0, 1000, "en", "tok", CancellationToken.None);

    public static TheoryData<HttpStatusCode, string?, TranscriptionErrorKind> Cases => new()
    {
        { HttpStatusCode.NotFound, null, TranscriptionErrorKind.Unavailable },
        { HttpStatusCode.MethodNotAllowed, null, TranscriptionErrorKind.Unavailable },
        { HttpStatusCode.ServiceUnavailable, "transcribe_unavailable", TranscriptionErrorKind.Unavailable },
        { HttpStatusCode.BadGateway, "transcribe_error", TranscriptionErrorKind.Transient },
        { HttpStatusCode.Unauthorized, "unauthorized", TranscriptionErrorKind.NeedsLogin },
        { HttpStatusCode.TooManyRequests, "transcribe_quota", TranscriptionErrorKind.Quota },
        { HttpStatusCode.TooManyRequests, "rate_limited", TranscriptionErrorKind.Transient },
        { HttpStatusCode.BadRequest, "bad_request", TranscriptionErrorKind.Failed },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StatusAndCode_MapToKind(HttpStatusCode status, string? code, TranscriptionErrorKind expected)
    {
        var response = code is null
            ? new HttpResponseMessage(status) { Content = new StringContent("Not Found") }
            : Json(status, new { error = code });

        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => Call(response));

        Assert.Equal(expected, ex.Kind);
    }
}

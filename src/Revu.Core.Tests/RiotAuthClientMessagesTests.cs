using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The sign-in failures <see cref="RiotAuthClient"/> turns into display copy follow the
/// house style (no em dashes, "sign in"), and none of them points at the retired
/// invite-code signup. Only the proxy HTTP hop is stubbed.
/// </summary>
public sealed class RiotAuthClientMessagesTests
{
    private sealed class Proxy(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"login_email_not_registered"}""", "This email isn't registered yet.")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_email"}""", "That email address doesn't look valid.")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"rate_limit_email"}""", "Too many attempts. Wait a minute and try again.")]
    [InlineData(HttpStatusCode.Unauthorized, "", "Session is no longer valid. Please sign in again.")]
    public async Task LoginFailure_ShowsHouseStyleCopy(HttpStatusCode status, string body, string expected)
    {
        var client = new RiotAuthClient(new HttpClient(new Proxy(status, body)), NullLogger<RiotAuthClient>.Instance);

        var ex = await Assert.ThrowsAsync<RiotAuthException>(() => client.LoginAsync("player@example.com"));

        Assert.Equal(expected, ex.Message);
        Assert.DoesNotContain('—', ex.Message);
        Assert.DoesNotContain("invite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

#nullable enable

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>Result of a verify-code call.</summary>
public sealed record RiotSessionResult(string SessionToken, long ExpiresAt);

/// <summary>Generic failure from the auth flow with a user-displayable message.</summary>
public sealed class RiotAuthException : Exception
{
    public RiotAuthException(string message) : base(message) { }
}

/// <summary>Resolved Riot account.</summary>
public sealed record RiotAccountResult(string Puuid, string GameName, string TagLine);

public interface IRiotAuthClient
{
    /// <summary>POST /auth/signup — sends magic-link email. Throws on failure.</summary>
    Task SignupAsync(string email, string inviteCode, CancellationToken ct = default);

    /// <summary>POST /auth/login — sends magic-link email. Throws on failure.</summary>
    Task LoginAsync(string email, CancellationToken ct = default);

    /// <summary>POST /auth/verify — exchanges OTP for a session token. Throws on failure.</summary>
    Task<RiotSessionResult> VerifyAsync(string code, CancellationToken ct = default);

    /// <summary>POST /auth/logout — invalidates the session. Best-effort; swallows network errors.</summary>
    Task LogoutAsync(string sessionToken, CancellationToken ct = default);

    /// <summary>
    /// GET /account — resolve a Riot ID (gameName#tagLine) to a PUUID.
    /// Requires an authenticated session token. Throws on failure.
    /// </summary>
    Task<RiotAccountResult> ResolveAccountAsync(
        string sessionToken,
        string riotId,
        string region,
        CancellationToken ct = default);
}

public sealed class RiotAuthClient : IRiotAuthClient
{
    private readonly HttpClient _http;
    private readonly ILogger<RiotAuthClient> _logger;

    public RiotAuthClient(HttpClient http, ILogger<RiotAuthClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task SignupAsync(string email, string inviteCode, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync(
            $"{RiotProxyEndpoint.BaseUrl}/auth/signup",
            new { email, inviteCode },
            ct).ConfigureAwait(false);
        await ThrowIfNotOkAsync(res, ct).ConfigureAwait(false);
    }

    public async Task LoginAsync(string email, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync(
            $"{RiotProxyEndpoint.BaseUrl}/auth/login",
            new { email },
            ct).ConfigureAwait(false);
        await ThrowIfNotOkAsync(res, ct).ConfigureAwait(false);
    }

    public async Task<RiotSessionResult> VerifyAsync(string code, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync(
            $"{RiotProxyEndpoint.BaseUrl}/auth/verify",
            new { code },
            ct).ConfigureAwait(false);
        await ThrowIfNotOkAsync(res, ct).ConfigureAwait(false);
        var body = await res.Content.ReadFromJsonAsync<VerifyResponseDto>(cancellationToken: ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.session_token))
        {
            throw new RiotAuthException("Server returned an empty session.");
        }
        return new RiotSessionResult(body.session_token, body.expires_at);
    }

    public async Task<RiotAccountResult> ResolveAccountAsync(
        string sessionToken,
        string riotId,
        string region,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"{RiotProxyEndpoint.BaseUrl}/account?riotId={Uri.EscapeDataString(riotId)}&region={Uri.EscapeDataString(region)}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
        var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            throw new RiotAuthException("Couldn't find that Riot account. Check the spelling + tag.");
        }
        await ThrowIfNotOkAsync(res, ct).ConfigureAwait(false);

        var body = await res.Content.ReadFromJsonAsync<AccountResponseDto>(cancellationToken: ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.puuid))
        {
            throw new RiotAuthException("Riot returned an empty account.");
        }
        return new RiotAccountResult(body.puuid, body.gameName ?? "", body.tagLine ?? "");
    }

    public async Task LogoutAsync(string sessionToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{RiotProxyEndpoint.BaseUrl}/auth/logout");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
            var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("Logout returned {Status}", res.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Logout request failed; clearing local session anyway");
        }
    }

    private static async Task ThrowIfNotOkAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;

        string? message = null;
        try
        {
            var err = await res.Content.ReadFromJsonAsync<ErrorDto>(cancellationToken: ct).ConfigureAwait(false);
            message = err?.message ?? err?.error;
        }
        catch
        {
            // body wasn't JSON
        }

        var friendly = res.StatusCode switch
        {
            HttpStatusCode.BadRequest => message switch
            {
                "invalid_email" => "That email address doesn't look valid.",
                "invite_code_required" => "An invite code is required to sign up.",
                "invite_code_invalid_or_used" => "That invite code is invalid or already used.",
                "invalid_or_expired_code" => "That code is invalid or expired. Request a new one.",
                "login_email_not_registered" => "This email isn't registered yet. Go back and enter an invite code to sign up.",
                "code_required" => "Please enter the code from your email.",
                _ => message ?? "The request was rejected.",
            },
            HttpStatusCode.TooManyRequests => "Too many attempts — wait a minute and try again.",
            HttpStatusCode.BadGateway => "Couldn't send the email right now. Try again in a moment.",
            HttpStatusCode.Unauthorized => "Session is no longer valid. Please log in again.",
            _ => message ?? $"Unexpected server error ({(int)res.StatusCode}).",
        };
        throw new RiotAuthException(friendly);
    }

    // ── DTOs ────────────────────────────────────────────────────────

    private sealed class VerifyResponseDto
    {
        public string session_token { get; set; } = "";
        public long expires_at { get; set; }
    }

    private sealed class ErrorDto
    {
        public string? error { get; set; }
        public string? message { get; set; }
    }

    private sealed class AccountResponseDto
    {
        public string puuid { get; set; } = "";
        public string? gameName { get; set; }
        public string? tagLine { get; set; }
    }
}

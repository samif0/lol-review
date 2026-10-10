#nullable enable

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>How a transcription call failed; drives the narration transcript status.</summary>
public enum TranscriptionErrorKind
{
    /// <summary>401/403: the session is missing or expired (status needs_login).</summary>
    NeedsLogin,
    /// <summary>429 transcribe_quota: the daily allowance is used up (status quota).</summary>
    Quota,
    /// <summary>429 rate_limited, 5xx, timeout or network: worth retrying.</summary>
    Transient,
    /// <summary>
    /// The server has no transcription yet: 404/405 (a proxy older than 3.14 without the
    /// route) or 503 transcribe_unavailable. Retried automatically at the next start.
    /// </summary>
    Unavailable,
    /// <summary>Anything else (status failed).</summary>
    Failed,
}

public sealed class TranscriptionException : Exception
{
    public TranscriptionErrorKind Kind { get; }

    public TranscriptionException(TranscriptionErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}

/// <summary>One transcribed chunk: segments already shifted by the request's offset.</summary>
public sealed record TranscriptionChunkResult(IReadOnlyList<TranscriptSegment> Segments, string Language);

/// <summary>Client for the proxy's <c>POST /transcribe</c> (C4 4.5).</summary>
public interface ITranscriptionClient
{
    Task<TranscriptionChunkResult> TranscribeChunkAsync(byte[] mp3, int offsetMs, int durationMs,
        string language, string token, CancellationToken ct);
}

public sealed class TranscriptionClient : ITranscriptionClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly ILogger<TranscriptionClient> _logger;
    private readonly string _baseUrl;

    public TranscriptionClient(HttpClient http, ILogger<TranscriptionClient> logger)
        : this(http, logger, RiotProxyEndpoint.BaseUrl)
    {
    }

    internal TranscriptionClient(HttpClient http, ILogger<TranscriptionClient> logger, string baseUrl)
    {
        _http = http;
        _logger = logger;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<TranscriptionChunkResult> TranscribeChunkAsync(byte[] mp3, int offsetMs, int durationMs,
        string language, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new TranscriptionException(TranscriptionErrorKind.NeedsLogin, "Sign in to get a transcript.");
        if (mp3 is null || mp3.Length == 0)
            throw new TranscriptionException(TranscriptionErrorKind.Failed, "Empty audio chunk.");

        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
        var url = $"{_baseUrl}/transcribe?offset_ms={offsetMs.ToString(CultureInfo.InvariantCulture)}"
            + $"&duration_ms={durationMs.ToString(CultureInfo.InvariantCulture)}&language={Uri.EscapeDataString(lang)}";

        var body = Convert.ToBase64String(mp3);
        using var content = new StringContent(body, Encoding.ASCII, "text/plain");
        content.Headers.ContentLength = Encoding.ASCII.GetByteCount(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new TranscriptionException(TranscriptionErrorKind.Transient, "The transcript request timed out.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogDebug(ex, "Transcribe request failed to send");
            throw new TranscriptionException(TranscriptionErrorKind.Transient, "Couldn't reach the server.", ex);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw await MapErrorAsync(res, linked.Token).ConfigureAwait(false);

            TranscribeResponseDto? dto;
            try
            {
                dto = await res.Content.ReadFromJsonAsync<TranscribeResponseDto>(cancellationToken: linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new TranscriptionException(TranscriptionErrorKind.Failed, "Server returned an unexpected response.", ex);
            }
            if (dto is null)
                throw new TranscriptionException(TranscriptionErrorKind.Failed, "Server returned an unexpected response.");

            var segments = (dto.segments ?? new List<SegmentDto>())
                .Where(s => s is not null)
                .Select(s => new TranscriptSegment(s.start, s.end, s.text ?? ""))
                .ToList();
            return new TranscriptionChunkResult(segments, dto.language ?? "");
        }
    }

    private static async Task<TranscriptionException> MapErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        string? code = null;
        string? message = null;
        try
        {
            var err = await res.Content.ReadFromJsonAsync<ErrorDto>(cancellationToken: ct).ConfigureAwait(false);
            code = err?.error;
            message = err?.message;
        }
        catch
        {
            // body was not JSON
        }

        var status = (int)res.StatusCode;
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new TranscriptionException(TranscriptionErrorKind.NeedsLogin, "Sign in to get a transcript.");
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
        {
            if (string.Equals(code, "transcribe_quota", StringComparison.OrdinalIgnoreCase))
                return new TranscriptionException(TranscriptionErrorKind.Quota,
                    string.IsNullOrWhiteSpace(message) ? "Daily transcript limit reached. Try again tomorrow." : message!);
            return new TranscriptionException(TranscriptionErrorKind.Transient, "Too many transcript requests.");
        }
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
            || string.Equals(code, "transcribe_unavailable", StringComparison.OrdinalIgnoreCase))
            return new TranscriptionException(TranscriptionErrorKind.Unavailable, "Transcription is not available on the server yet.");
        if (status >= 500)
            return new TranscriptionException(TranscriptionErrorKind.Transient, $"Transcription is temporarily unavailable ({status}).");
        return new TranscriptionException(TranscriptionErrorKind.Failed,
            code is not null ? $"Transcription failed ({code})." : $"Transcription failed ({status}).");
    }

    private sealed class TranscribeResponseDto
    {
        public string? language { get; set; }
        public double duration_s { get; set; }
        public string? text { get; set; }
        public List<SegmentDto>? segments { get; set; }
    }

    private sealed class SegmentDto
    {
        public double start { get; set; }
        public double end { get; set; }
        public string? text { get; set; }
    }

    private sealed class ErrorDto
    {
        public string? error { get; set; }
        public string? message { get; set; }
    }
}

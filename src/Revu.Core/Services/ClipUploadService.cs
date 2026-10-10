#nullable enable

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Uploads local clip files to the Revu Worker (<c>/clips</c>) for public sharing.
/// Mirrors <see cref="RiotAuthClient"/>: constructor-injected <see cref="HttpClient"/>,
/// fixed endpoint from <see cref="RiotProxyEndpoint"/>, friendly exceptions.
/// <para>
/// The injected HttpClient has no timeout of its own (a 2 GB upload runs for many
/// minutes); every request here carries a linked timeout instead: 10 min for a legacy
/// upload, 30 s for multipart init, 5 min per part, 2 min per complete, 10 s for delete.
/// </para>
/// </summary>
public sealed partial class ClipUploadService : IClipUploadService
{
    // Keep in sync with proxy/src/clips.ts. The legacy single POST is used at or below
    // MultipartThresholdBytes (95 MiB, under the proxy's 100 MiB legacy cap); larger files
    // go through R2 multipart, up to 2 GiB. The sidecar caps clips at 600 s before here.
    internal const long MaxClipBytes = 2147483648L;
    internal const long MultipartThresholdBytes = 99614720L;

    private static readonly TimeSpan LegacyTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TranscriptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(10);

    private const string TooLargeMessage = "Clip is too large to share (2 GB max).";
    private const string UnavailableMessage = "Sharing is temporarily unavailable. Try again in a moment.";
    private const string TimeoutMessage = "The upload timed out. Check your connection and try again.";
    private const string NetworkMessage = "Couldn't reach the server. Check your connection.";

    private readonly HttpClient _http;
    private readonly ILogger<ClipUploadService> _logger;
    private readonly string _baseUrl;

    public ClipUploadService(HttpClient http, ILogger<ClipUploadService> logger)
        : this(http, logger, RiotProxyEndpoint.BaseUrl)
    {
    }

    internal ClipUploadService(HttpClient http, ILogger<ClipUploadService> logger, string baseUrl)
    {
        _http = http;
        _logger = logger;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<ClipUploadResult> UploadAsync(
        string filePath,
        string sessionToken,
        string? title = null,
        string? champion = null,
        int? durationSeconds = null,
        IProgress<ClipUploadProgress>? progress = null,
        bool narrated = false,
        Action<string>? onRemoteIdAssigned = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            throw new ClipUploadException("You need to be logged in to share clips.", unauthorized: true);
        }
        if (!File.Exists(filePath))
        {
            throw new ClipUploadException("Clip file is missing. Save the clip again.");
        }

        var info = new FileInfo(filePath);
        if (info.Length == 0)
        {
            throw new ClipUploadException("That clip file is empty.");
        }
        if (info.Length > MaxClipBytes)
        {
            throw new ClipUploadException(TooLargeMessage);
        }

        var contentType = ContentTypeFor(filePath)
            ?? throw new ClipUploadException("Only MP4 and WebM clips can be shared.");

        return info.Length <= MultipartThresholdBytes
            ? await UploadLegacyAsync(filePath, info.Length, contentType, sessionToken, title, champion,
                durationSeconds, narrated, progress, ct).ConfigureAwait(false)
            : await UploadMultipartAsync(filePath, info.Length, contentType, sessionToken, title, champion,
                durationSeconds, narrated, progress, onRemoteIdAssigned, ct).ConfigureAwait(false);
    }

    private async Task<ClipUploadResult> UploadLegacyAsync(string filePath, long size, string contentType,
        string sessionToken, string? title, string? champion, int? durationSeconds, bool narrated,
        IProgress<ClipUploadProgress>? progress, CancellationToken ct)
    {
        var query = MetadataQuery(title, champion, durationSeconds);
        if (narrated) query.Add("narrated=1");
        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";

        // Stream the file rather than buffering it all in memory. FileShare.Delete lets a
        // narration change delete the file under a running upload (the job is cancelled).
        await using var stream = OpenShared(filePath);
        await using var counting = new ProgressReadStream(stream,
            sent => progress?.Report(new ClipUploadProgress("uploading", sent, size)));

        using var content = new StreamContent(counting, 1 << 16);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = size;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/clips{qs}") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        progress?.Report(new ClipUploadProgress("uploading", 0, size));

        using var timeout = new CancellationTokenSource(LegacyTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new ClipUploadException(TimeoutMessage, ex, retryable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogError(ex, "Clip upload request failed to send");
            throw new ClipUploadException(NetworkMessage, ex, retryable: true);
        }

        using (res)
        {
            await ThrowIfNotOkAsync(res, retryableContext: true, linked.Token).ConfigureAwait(false);
            var result = await ReadResultAsync(res, linked.Token).ConfigureAwait(false);
            progress?.Report(new ClipUploadProgress("uploading", size, size));
            _logger.LogInformation("Clip uploaded: {Id}", result.Id);
            return result;
        }
    }

    /// <summary>Delete a previously-uploaded clip (owner-only on the server).</summary>
    public async Task<bool> DeleteAsync(string clipId, string sessionToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clipId) || string.IsNullOrWhiteSpace(sessionToken)) return false;
        try
        {
            using var timeout = new CancellationTokenSource(DeleteTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/clips/{Uri.EscapeDataString(clipId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
            using var res = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
            if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotFound) return true;
            _logger.LogDebug("Clip delete returned {Status}", res.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clip delete request failed");
            return false;
        }
    }

    public async Task PutTranscriptAsync(string clipId, string sessionToken, TranscriptDocument doc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clipId)) throw new ClipUploadException("No shared clip to attach the transcript to.");
        if (string.IsNullOrWhiteSpace(sessionToken))
            throw new ClipUploadException("You need to be logged in to share clips.", unauthorized: true);

        using var content = new StringContent(doc.ToJson(), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/clips/{Uri.EscapeDataString(clipId)}/transcript")
        {
            Content = content,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        using var timeout = new CancellationTokenSource(TranscriptTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new ClipUploadException(TimeoutMessage, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ClipUploadException(NetworkMessage, ex);
        }
        using (res)
        {
            // 405: a proxy older than 3.14 has no transcript route. 404: the clip is gone.
            if (res.StatusCode == HttpStatusCode.MethodNotAllowed) throw new ClipUploadException(UnavailableMessage);
            if (res.StatusCode == HttpStatusCode.NotFound)
                throw new ClipUploadException("The shared clip is no longer available.") { Gone = true };
            await ThrowIfNotOkAsync(res, retryableContext: false, linked.Token).ConfigureAwait(false);
        }
    }

    private static List<string> MetadataQuery(string? title, string? champion, int? durationSeconds)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) query.Add($"title={Uri.EscapeDataString(title.Trim())}");
        if (!string.IsNullOrWhiteSpace(champion)) query.Add($"champion={Uri.EscapeDataString(champion.Trim())}");
        if (durationSeconds is > 0) query.Add($"duration={durationSeconds.Value.ToString(CultureInfo.InvariantCulture)}");
        return query;
    }

    private static FileStream OpenShared(string filePath) => new(
        filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
        bufferSize: 1 << 16, useAsync: true);

    private static async Task<ClipUploadResult> ReadResultAsync(HttpResponseMessage res, CancellationToken ct)
    {
        UploadResponseDto? body;
        try
        {
            body = await res.Content.ReadFromJsonAsync<UploadResponseDto>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClipUploadException("Server returned an unexpected response.", ex);
        }
        if (body is null || string.IsNullOrWhiteSpace(body.id) || string.IsNullOrWhiteSpace(body.url))
        {
            throw new ClipUploadException("Server returned an unexpected response.");
        }
        return new ClipUploadResult(body.id, body.url, body.expires_at);
    }

    private static string? ContentTypeFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => null,
        };
    }

    /// <summary>
    /// Map a non-2xx response to a <see cref="ClipUploadException"/>. Retryable is set only
    /// for 5xx when <paramref name="retryableContext"/> (legacy upload or multipart init).
    /// </summary>
    private static async Task ThrowIfNotOkAsync(HttpResponseMessage res, bool retryableContext, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;

        // Read the machine-readable error CODE and the human MESSAGE separately. The
        // code disambiguates 403s that the status alone conflates: the proxy returns
        // 403 for BOTH "login_required" (no account) AND "quota_exceeded" (active-clip
        // cap reached). Branch on the code first.
        string? code = null;
        string? serverMessage = null;
        try
        {
            var err = await res.Content.ReadFromJsonAsync<ErrorDto>(cancellationToken: ct).ConfigureAwait(false);
            code = err?.error;
            serverMessage = err?.message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // body wasn't JSON
        }

        var status = (int)res.StatusCode;
        var retryable = retryableContext && status >= 500;

        if (string.Equals(code, "quota_exceeded", StringComparison.OrdinalIgnoreCase))
        {
            var msg = !string.IsNullOrWhiteSpace(serverMessage)
                ? serverMessage!
                : "Clip-share limit reached (150 active clips). Delete old shared clips, or let them expire.";
            throw new ClipUploadException(msg);
        }
        if (string.Equals(code, "too_many_pending_uploads", StringComparison.OrdinalIgnoreCase))
        {
            throw new ClipUploadException("Another long clip is still uploading. Try again when it finishes.");
        }

        var unauthorized = string.Equals(code, "login_required", StringComparison.OrdinalIgnoreCase)
            || res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        var friendly = res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your login expired. Log in again to share.",
            HttpStatusCode.Forbidden => "You need to be logged in to share clips.",
            HttpStatusCode.RequestEntityTooLarge => TooLargeMessage,
            HttpStatusCode.UnsupportedMediaType => "Only MP4 and WebM clips can be shared.",
            HttpStatusCode.TooManyRequests => "Too many uploads. Wait a moment and try again.",
            // 5xx (incl. the proxy's clip_error and a raw Cloudflare 503) are transient
            // server-side failures: tell the user to retry rather than showing a bare code.
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                => UnavailableMessage,
            _ when status >= 500 => UnavailableMessage,
            _ => code is not null ? $"Upload failed ({code})." : $"Upload failed ({status}).",
        };
        throw new ClipUploadException(friendly, unauthorized, retryable);
    }

    private sealed class UploadResponseDto
    {
        public string id { get; set; } = "";
        public string url { get; set; } = "";
        public long expires_at { get; set; }
    }

    private sealed class ErrorDto
    {
        public string? error { get; set; }
        public string? message { get; set; }
    }
}

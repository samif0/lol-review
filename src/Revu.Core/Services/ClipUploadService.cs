#nullable enable

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Uploads local clip files to the Revu Worker (<c>/clips</c>) for public sharing.
/// Mirrors <see cref="RiotAuthClient"/>: constructor-injected <see cref="HttpClient"/>,
/// fixed endpoint from <see cref="RiotProxyEndpoint"/>, friendly exceptions.
/// </summary>
public sealed class ClipUploadService : IClipUploadService
{
    // Keep in sync with the server cap in proxy/src/clips.ts (MAX_CLIP_BYTES).
    // The desktop also enforces a 90-second duration limit before calling here.
    private const long MaxClipBytes = 200L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<ClipUploadService> _logger;

    public ClipUploadService(HttpClient http, ILogger<ClipUploadService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ClipUploadResult> UploadAsync(
        string filePath,
        string sessionToken,
        string? title = null,
        string? champion = null,
        int? durationSeconds = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            throw new ClipUploadException("You need to be logged in to share clips.");
        }
        if (!File.Exists(filePath))
        {
            throw new ClipUploadException("That clip file no longer exists on disk.");
        }

        var info = new FileInfo(filePath);
        if (info.Length == 0)
        {
            throw new ClipUploadException("That clip file is empty.");
        }
        if (info.Length > MaxClipBytes)
        {
            throw new ClipUploadException("Clip is too large to share (200 MB max). Trim it and re-clip.");
        }

        var contentType = ContentTypeFor(filePath);
        if (contentType is null)
        {
            throw new ClipUploadException("Only MP4 and WebM clips can be shared.");
        }

        // Build the query string with the optional metadata.
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) query.Add($"title={Uri.EscapeDataString(title.Trim())}");
        if (!string.IsNullOrWhiteSpace(champion)) query.Add($"champion={Uri.EscapeDataString(champion.Trim())}");
        if (durationSeconds is > 0) query.Add($"duration={durationSeconds.Value}");
        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";

        // Stream the file rather than buffering it all in memory.
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, useAsync: true);

        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = info.Length;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{RiotProxyEndpoint.BaseUrl}/clips{qs}")
        {
            Content = content,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        progress?.Report(0);

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clip upload request failed to send");
            throw new ClipUploadException("Couldn't reach the server. Check your connection.", ex);
        }

        using (res)
        {
            await ThrowIfNotOkAsync(res, ct).ConfigureAwait(false);

            var body = await res.Content.ReadFromJsonAsync<UploadResponseDto>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.id) || string.IsNullOrWhiteSpace(body.url))
            {
                throw new ClipUploadException("Server returned an unexpected response.");
            }

            progress?.Report(1);
            _logger.LogInformation("Clip uploaded: {Id}", body.id);
            return new ClipUploadResult(body.id, body.url, body.expires_at);
        }
    }

    /// <summary>Delete a previously-uploaded clip (owner-only on the server).</summary>
    public async Task DeleteAsync(string clipId, string sessionToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clipId) || string.IsNullOrWhiteSpace(sessionToken)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{RiotProxyEndpoint.BaseUrl}/clips/{Uri.EscapeDataString(clipId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("Clip delete returned {Status}", res.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clip delete request failed");
        }
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

    private static async Task ThrowIfNotOkAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;

        string? code = null;
        try
        {
            var err = await res.Content.ReadFromJsonAsync<ErrorDto>(cancellationToken: ct).ConfigureAwait(false);
            code = err?.error ?? err?.message;
        }
        catch
        {
            // body wasn't JSON
        }

        var friendly = res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your login expired. Log in again to share.",
            HttpStatusCode.Forbidden => "You need to be logged in to share clips.",
            HttpStatusCode.RequestEntityTooLarge => "Clip is too large to share (200 MB max).",
            (HttpStatusCode)415 => "Only MP4 and WebM clips can be shared.",
            HttpStatusCode.TooManyRequests => "Too many uploads — wait a moment and try again.",
            _ => code is not null ? $"Upload failed ({code})." : $"Upload failed ({(int)res.StatusCode}).",
        };
        throw new ClipUploadException(friendly);
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

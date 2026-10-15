#nullable enable

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using Revu.Core.Constants;

namespace Revu.Core.Services;

/// <summary>
/// Full-game (VOD) sharing against the proxy's <c>/vod-shares</c> routes (4.2): status, an
/// always-multipart upload that reuses the clip part loop and complete under the
/// <c>vod-shares</c> route base, and the markers document. Every failure is a
/// <see cref="ClipUploadException"/> whose <see cref="ClipUploadException.Code"/> the worker maps.
/// </summary>
public sealed partial class ClipUploadService : IVodShareClient
{
    internal const string VodRouteBase = "vod-shares";

    private static readonly TimeSpan VodStatusTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VodMarkersTimeout = TimeSpan.FromSeconds(30);

    internal const string VodSignInMessage = "Sign in to share this game.";
    internal const string VodNetworkMessage = "Couldn't reach Revu. Check your connection and try again.";
    internal const string VodUploadFailedMessage = "The upload didn't finish. Check your connection and try again.";

    public async Task<VodShareServerStatus> GetVodShareStatusAsync(string sessionToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) throw VodError("unauthorized");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/{VodRouteBase}/status");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        using var res = await SendVodAsync(req, VodStatusTimeout, ct).ConfigureAwait(false);

        // A proxy older than 4.2 has no VOD routes: GET falls through to its 404/405 guards.
        if (res.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            return Unsupported();
        if (!res.Response.IsSuccessStatusCode) throw await VodErrorAsync(res.Response, res.Token).ConfigureAwait(false);

        StatusDto? dto;
        try
        {
            dto = await res.Response.Content.ReadFromJsonAsync<StatusDto>(cancellationToken: res.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw VodError("network", inner: ex);
        }
        if (dto is null) throw VodError("network");
        return dto.ToStatus();
    }

    public async Task<VodUploadResult> UploadVodAsync(string filePath, string sessionToken, VodUploadRequest request,
        IProgress<ClipUploadProgress>? progress = null, Action<VodUploadStarted>? onStarted = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateVodRequest(sessionToken, request);
        if (!File.Exists(filePath)) throw VodError("upload_failed", "The prepared video is missing. Share again.");
        var size = new FileInfo(filePath).Length;
        if (size <= 0) throw VodError("upload_failed", "The prepared video is empty. Share again.");
        return await UploadVodCoreAsync(filePath, size, null, sessionToken, request, progress, onStarted, ct)
            .ConfigureAwait(false);
    }

    public async Task<VodUploadResult> UploadVodWhileWritingAsync(string filePath, long declaredSize, Task<bool> written,
        string sessionToken, VodUploadRequest request, IProgress<ClipUploadProgress>? progress = null,
        Action<VodUploadStarted>? onStarted = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(written);
        ValidateVodRequest(sessionToken, request);
        if (declaredSize <= 0) throw VodError("upload_failed", "The prepared video is empty. Share again.");
        return await UploadVodCoreAsync(filePath, declaredSize, written, sessionToken, request, progress, onStarted, ct)
            .ConfigureAwait(false);
    }

    private static void ValidateVodRequest(string sessionToken, VodUploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) throw VodError("unauthorized");
        if (request is null || request.Quality is not ("1080p" or "720p"))
            throw VodError("bad_request", "Unsupported share quality.");
    }

    private async Task<VodUploadResult> UploadVodCoreAsync(string filePath, long size, Task<bool>? written,
        string sessionToken, VodUploadRequest request, IProgress<ClipUploadProgress>? progress,
        Action<VodUploadStarted>? onStarted, CancellationToken ct)
    {
        if (size > GameConstants.MaxVodShareBytes) throw VodError("payload_too_large");
        var duration = Math.Clamp(request.DurationSeconds, 1, GameConstants.MaxVodShareSeconds);

        var fps = VodShareEncoderArgs.Fps(request.Fps);
        var init = await VodInitAsync(size, request.Quality, fps, duration, sessionToken, ct).ConfigureAwait(false);
        // The proxy shows an uploading VOD's progress at its link, so the link is good from init on.
        var started = new VodUploadStarted(init.id, init.url ?? "", init.share_expires_at);
        Action<string>? assigned = onStarted is null ? null : _ => onStarted(started);
        return await ContinueVodUploadAsync(init.id, init.part_size, init.part_count, init.share_expires_at, filePath, size,
            written, sessionToken, progress, assigned, ct).ConfigureAwait(false);
    }

    public async Task<VodUploadResult> UploadVodWhileWritingAsync(VodMp4Reservation reserved, string filePath,
        Task<bool> written, string sessionToken, IProgress<ClipUploadProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reserved);
        ArgumentNullException.ThrowIfNull(written);
        if (string.IsNullOrWhiteSpace(sessionToken)) throw VodError("unauthorized");
        return await ContinueVodUploadAsync(reserved.Id, reserved.PartSize, reserved.PartCount, reserved.ShareExpiresAt,
            filePath, reserved.DeclaredSize, written, sessionToken, progress, null, ct).ConfigureAwait(false);
    }

    /// <summary>The parts and complete of a VOD row whose init already succeeded.</summary>
    private async Task<VodUploadResult> ContinueVodUploadAsync(string id, long partSize, int partCount, long shareExpiresAt,
        string filePath, long size, Task<bool>? written, string sessionToken, IProgress<ClipUploadProgress>? progress,
        Action<string>? assigned, CancellationToken ct)
    {
        try
        {
            var done = await UploadPartsAndCompleteAsync(VodRouteBase, id, partSize, partCount, filePath,
                size, sessionToken, progress, assigned, "VOD", ct, written).ConfigureAwait(false);
            return new VodUploadResult(done.Id, done.Url, done.ExpiresAt > 0 ? done.ExpiresAt : shareExpiresAt);
        }
        catch (ClipUploadException ex) when (ex.Code is null)
        {
            // Parts and complete were already retried: terminal. A 401 keeps its sign-in meaning.
            throw ex.Unauthorized
                ? VodError("unauthorized", inner: ex)
                : VodError("upload_failed", inner: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw VodError("upload_failed", inner: ex);
        }
    }

    public async Task PutVodMarkersAsync(string id, string sessionToken, string markersJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw VodError("not_found", "No shared game to attach the markers to.");
        if (string.IsNullOrWhiteSpace(sessionToken)) throw VodError("unauthorized");

        using var content = new StringContent(markersJson ?? "", Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{_baseUrl}/{VodRouteBase}/{Uri.EscapeDataString(id)}/markers") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        using var res = await SendVodAsync(req, VodMarkersTimeout, ct).ConfigureAwait(false);
        if (!res.Response.IsSuccessStatusCode) throw await VodErrorAsync(res.Response, res.Token).ConfigureAwait(false);
    }

    private async Task<VodInitDto> VodInitAsync(long size, string quality, int fps, int duration, string sessionToken,
        CancellationToken ct)
    {
        var query = string.Join("&",
            $"size={size.ToString(CultureInfo.InvariantCulture)}",
            $"duration={duration.ToString(CultureInfo.InvariantCulture)}",
            $"quality={Uri.EscapeDataString(quality)}",
            $"fps={fps.ToString(CultureInfo.InvariantCulture)}");
        using var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        content.Headers.ContentLength = 0;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{VodRouteBase}/uploads?{query}")
        {
            Content = content,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        using var res = await SendVodAsync(req, InitTimeout, ct).ConfigureAwait(false);
        if (!res.Response.IsSuccessStatusCode) throw await VodErrorAsync(res.Response, res.Token).ConfigureAwait(false);

        VodInitDto? body;
        try
        {
            body = await res.Response.Content.ReadFromJsonAsync<VodInitDto>(cancellationToken: res.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw VodError("upload_failed", "Server returned an unexpected response.", inner: ex);
        }
        if (body is null || string.IsNullOrWhiteSpace(body.id))
            throw VodError("upload_failed", "Server returned an unexpected response.");
        if (body.part_count <= 0 || body.part_size <= 0 || body.part_size > MaxPartBytes)
        {
            // The upload exists remotely: drop it before failing.
            await DeleteAsync(body.id, sessionToken, CancellationToken.None).ConfigureAwait(false);
            throw VodError("upload_failed", "Server returned an unexpected response.");
        }
        return body;
    }

    /// <summary>One VOD request with its own timeout. Send failures become Code <c>network</c>.</summary>
    private async Task<VodResponse> SendVodAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
    {
        var timer = new CancellationTokenSource(timeout);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            var res = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
            return new VodResponse(res, timer, linked);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            linked.Dispose();
            timer.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            linked.Dispose();
            timer.Dispose();
            _logger.LogInformation(ex, "VOD share request {Method} {Path} failed to send", req.Method, req.RequestUri?.AbsolutePath);
            throw VodError("network", inner: ex);
        }
    }

    /// <summary>Map a non-2xx VOD response (contract 7.2): 401 unauthorized, 5xx network, else the proxy code.</summary>
    private static async Task<ClipUploadException> VodErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var status = (int)res.StatusCode;
        if (res.StatusCode == HttpStatusCode.Unauthorized) return VodError("unauthorized");

        VodErrorDto? err = null;
        try
        {
            err = await res.Content.ReadFromJsonAsync<VodErrorDto>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // body wasn't JSON
        }
        var code = string.IsNullOrWhiteSpace(err?.error) ? $"http_{status}" : err!.error!.Trim();
        // The kill switch answers 503 on purpose; every other 5xx is a transient server failure.
        if (code == "vod_sharing_disabled") return VodError(code);
        if (status >= 500) return VodError("network");
        if (code is "unauthorized" or "login_required") return VodError("unauthorized");
        var retryAt = err?.retry_at is > 0 ? err.retry_at : null;
        return VodError(code, KnownVodMessage(code) ?? NonEmpty(err?.message), retryAt);
    }

    private static ClipUploadException VodError(string code, string? message = null, long? retryAt = null,
        Exception? inner = null)
    {
        var text = message ?? KnownVodMessage(code) ?? "Sharing didn't finish. Try again.";
        var unauthorized = code == "unauthorized";
        var retryable = code == "network";
        var ex = inner is null
            ? new ClipUploadException(text, unauthorized, retryable) { Code = code, RetryAt = retryAt }
            : new ClipUploadException(text, inner, unauthorized, retryable) { Code = code, RetryAt = retryAt };
        return ex;
    }

    /// <summary>Fallback copy per code (the desktop UI shows its own; this is only the fallback).</summary>
    internal static string? KnownVodMessage(string code) => code switch
    {
        "unauthorized" => VodSignInMessage,
        "network" or "timeout" => VodNetworkMessage,
        "upload_failed" => VodUploadFailedMessage,
        "not_allowed" => "Full-game sharing isn't available for this account.",
        "vod_sharing_disabled" => "Full-game sharing is paused right now. Try again later.",
        "pool_full" => "Sharing space is full right now. Try 720p, or save the file instead.",
        "live_limit" => "You already share 3 games. Stop sharing one to share this game.",
        "daily_limit" => "You've reached today's limit for shared games.",
        "too_many_pending_uploads" => "Another game is still being shared. Try again when it finishes.",
        "rate_limited" => "Too many requests. Wait a moment and try again.",
        "payload_too_large" => "This game is too large to share, even at 720p.",
        _ => null,
    };

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static VodShareServerStatus Unsupported() =>
        new(false, false, false, false, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null, null,
            Array.Empty<VodShareServerShare>());

    /// <summary>A response plus the timeout sources that must outlive reading its body.</summary>
    private sealed class VodResponse(HttpResponseMessage response, CancellationTokenSource timer,
        CancellationTokenSource linked) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public CancellationToken Token => linked.Token;

        public void Dispose()
        {
            Response.Dispose();
            linked.Dispose();
            timer.Dispose();
        }
    }

    // ── Wire DTOs (proxy JSON is snake_case) ─────────────────────────────────

    private sealed class VodInitDto
    {
        public string id { get; set; } = "";
        public long part_size { get; set; }
        public int part_count { get; set; }
        public long expires_at { get; set; }
        public long share_expires_at { get; set; }
        public string url { get; set; } = "";
    }

    private sealed class VodErrorDto
    {
        public string? error { get; set; }
        public string? message { get; set; }
        public long? retry_at { get; set; }
    }

    private sealed class StatusDto
    {
        public bool allowed { get; set; }
        public bool sharing { get; set; }
        public bool serving { get; set; }
        public long now { get; set; }
        public LimitsDto? limits { get; set; }
        public UsageDto? usage { get; set; }
        public PoolDto? pool { get; set; }
        public List<ShareDto>? shares { get; set; }
        public HlsDto? hls { get; set; }

        public VodShareServerStatus ToStatus() => new(
            Supported: true,
            Allowed: allowed,
            Sharing: sharing,
            Serving: serving,
            Now: now > 0 ? now : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Limits: allowed ? limits?.ToLimits() : null,
            Usage: allowed ? usage?.ToUsage() : null,
            Pool: allowed ? pool?.ToPool() : null,
            Shares: (shares ?? new List<ShareDto>())
                .Where(s => !string.IsNullOrWhiteSpace(s.id))
                .Select(s => new VodShareServerShare(s.id!.Trim(), s.status ?? "", s.quality, s.size_bytes, s.created_at,
                    s.expires_at, s.format == "hls" ? "hls" : "mp4", s.format == "hls" ? s.segments : null, s.paused == true))
                .ToList(),
            Hls: allowed ? hls?.ToHls() : null);
    }

    private sealed class HlsDto
    {
        public bool enabled { get; set; }
        public int segment_seconds { get; set; }
        public int target_duration { get; set; }
        public long max_segment_bytes { get; set; }
        public int init_max_bytes { get; set; }
        public int playable_ms { get; set; }
        public int max_runs { get; set; }

        /// <summary>Settings the desktop was not built for read as "off": it only speaks 2 s segments.</summary>
        public VodShareHlsInfo ToHls() => new(
            enabled && segment_seconds == VodShareEncoderArgs.HlsSegmentSeconds && max_segment_bytes > 0 && init_max_bytes > 0
                && max_runs > 0,
            segment_seconds, target_duration, max_segment_bytes, init_max_bytes, playable_ms, max_runs);
    }

    private sealed class LimitsDto
    {
        public long max_bytes { get; set; }
        public int max_seconds { get; set; }
        public long ttl_seconds { get; set; }
        public int max_live { get; set; }
        public int max_pending { get; set; }
        public int max_starts_per_day { get; set; }
        public long part_size { get; set; }
        public int markers_max_bytes { get; set; }
        public int notes_max_bytes { get; set; }
        public int daily_play_cap { get; set; }

        public VodShareServerLimits ToLimits() => new(max_bytes, max_seconds, ttl_seconds, max_live, max_pending,
            max_starts_per_day, part_size, markers_max_bytes, notes_max_bytes, daily_play_cap);
    }

    private sealed class UsageDto
    {
        public int live { get; set; }
        public int pending { get; set; }
        public int starts_today { get; set; }
        public long day_resets_at { get; set; }

        public VodShareServerUsage ToUsage() => new(live, pending, starts_today, day_resets_at);
    }

    private sealed class PoolDto
    {
        public long total_budget_bytes { get; set; }
        public long vod_budget_bytes { get; set; }
        public long used_bytes { get; set; }
        public long vod_used_bytes { get; set; }
        public long free_bytes { get; set; }
        public List<ReleaseDto>? releases { get; set; }

        public VodSharePool ToPool() => new(total_budget_bytes, vod_budget_bytes, used_bytes, vod_used_bytes, free_bytes,
            (releases ?? new List<ReleaseDto>()).Select(r => new VodSharePoolRelease(r.at, r.bytes, r.vod_bytes))
                .OrderBy(r => r.At).ToList());
    }

    private sealed class ReleaseDto
    {
        public long at { get; set; }
        public long bytes { get; set; }
        public long vod_bytes { get; set; }
    }

    private sealed class ShareDto
    {
        public string? id { get; set; }
        public string? status { get; set; }
        public string? quality { get; set; }
        public long size_bytes { get; set; }
        public long created_at { get; set; }
        public long expires_at { get; set; }
        public string? format { get; set; }
        public int? segments { get; set; }
        public bool? paused { get; set; }
    }
}

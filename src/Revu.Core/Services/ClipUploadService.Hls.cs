#nullable enable

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Revu.Core.Constants;

namespace Revu.Core.Services;

/// <summary>
/// 4.4: a full game shared as HLS segments that play while they upload (plan 4.4). The init reserves the share with
/// <c>format=hls</c>; the segments, runs, pause and end go through the <see cref="VodHlsUploadSession"/> it returns.
/// An older proxy ignores <c>format</c> and answers with an MP4 multipart row, which today's path then uses.
/// </summary>
public sealed partial class ClipUploadService
{
    public async Task<VodHlsStart> StartVodHlsUploadAsync(string sessionToken, VodUploadRequest request, long declaredSize,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateVodRequest(sessionToken, request);
        if (declaredSize <= 0) throw VodError("upload_failed", "The prepared video is empty. Share again.");
        if (declaredSize > GameConstants.MaxVodShareBytes) throw VodError("payload_too_large");
        var duration = Math.Clamp(request.DurationSeconds, 1, GameConstants.MaxVodShareSeconds);
        var fps = VodShareEncoderArgs.Fps(request.Fps);

        var query = string.Join("&",
            $"size={declaredSize.ToString(CultureInfo.InvariantCulture)}",
            $"duration={duration.ToString(CultureInfo.InvariantCulture)}",
            $"quality={Uri.EscapeDataString(request.Quality)}",
            $"fps={fps.ToString(CultureInfo.InvariantCulture)}",
            "format=hls");
        using var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        content.Headers.ContentLength = 0;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{VodRouteBase}/uploads?{query}") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        using var res = await SendVodAsync(req, InitTimeout, ct).ConfigureAwait(false);
        if (!res.Response.IsSuccessStatusCode) throw await VodErrorAsync(res.Response, res.Token).ConfigureAwait(false);

        HlsInitDto? body;
        try
        {
            body = await res.Response.Content.ReadFromJsonAsync<HlsInitDto>(cancellationToken: res.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw VodError("upload_failed", "Server returned an unexpected response.", inner: ex);
        }
        if (body is null || string.IsNullOrWhiteSpace(body.id)) throw VodError("upload_failed", "Server returned an unexpected response.");
        var started = new VodUploadStarted(body.id, body.url ?? "", body.share_expires_at);

        if (body.format == "hls")
        {
            if (body.part_count <= 0 || body.segment_seconds != VodShareEncoderArgs.HlsSegmentSeconds
                || body.max_segment_bytes <= 0 || body.init_max_bytes <= 0 || body.max_runs <= 0)
            {
                await DeleteAsync(body.id, sessionToken, CancellationToken.None).ConfigureAwait(false);
                throw VodError("upload_failed", "Server returned an unexpected response.");
            }
            var info = new VodHlsSessionInfo(body.id, body.url ?? "", body.share_expires_at, body.expires_at, body.part_count,
                body.max_segment_bytes, body.init_max_bytes, body.playable_ms > 0 ? body.playable_ms : 8000, body.max_runs);
            var session = new VodHlsUploadSession(_http, _logger, _baseUrl, sessionToken, info, VodRetry,
                token => DeleteAsync(body.id, sessionToken, token))
            {
                Fps = fps,
                Quality = request.Quality == "720p" ? VodShareQuality.P720 : VodShareQuality.P1080,
            };
            _logger.LogInformation("HLS share {Id} reserved: {Slots} segment slots, {Bytes} bytes", body.id, body.part_count, declaredSize);
            return new VodHlsStart(started, session, null);
        }

        // An older proxy: today's multipart row, used as it is (the daily start is spent already).
        var expectedParts = body.part_size <= 0 ? -1 : (int)((declaredSize + body.part_size - 1) / body.part_size);
        if (body.part_size <= 0 || body.part_size > MaxPartBytes || body.part_count != expectedParts)
        {
            await DeleteAsync(body.id, sessionToken, CancellationToken.None).ConfigureAwait(false);
            throw VodError("upload_failed", "Server returned an unexpected response.");
        }
        _logger.LogInformation("Share {Id}: the proxy answered an HLS init with an MP4 upload; continuing as MP4", body.id);
        return new VodHlsStart(started, null,
            new VodMp4Reservation(body.id, body.part_size, body.part_count, declaredSize, body.share_expires_at, body.url ?? ""));
    }

    private sealed class HlsInitDto
    {
        public string id { get; set; } = "";
        public string? format { get; set; }
        public int part_count { get; set; }
        public long part_size { get; set; }
        public int segment_seconds { get; set; }
        public int target_duration { get; set; }
        public long max_segment_bytes { get; set; }
        public int init_max_bytes { get; set; }
        public int playable_ms { get; set; }
        public int max_runs { get; set; }
        public long expires_at { get; set; }
        public long share_expires_at { get; set; }
        public string? url { get; set; }
    }
}

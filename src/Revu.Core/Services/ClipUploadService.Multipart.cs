#nullable enable

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// R2 multipart sharing for clips above 95 MiB (C4 4.1 to 4.4): init, sequential parts of
/// the server-chosen size, complete. Any terminal failure or cancellation DELETEs the
/// remote upload with its own 10 s timeout (2 s once the caller's token has fired).
/// </summary>
public sealed partial class ClipUploadService
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PartTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long the remote abort may take once the caller's token has fired.</summary>
    internal static readonly TimeSpan AbortBudgetAfterCancel = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Largest part size the client accepts from init (the proxy sends 16 MiB). A part is
    /// held in one pooled buffer, so an absurd size is refused rather than allocated.
    /// </summary>
    internal const long MaxPartBytes = 256L * 1024 * 1024;

    /// <summary>Delays between the 4 attempts of one part (tests shorten these).</summary>
    internal TimeSpan[] PartRetryDelays { get; set; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>Delays between the 3 attempts of complete (tests shorten these).</summary>
    internal TimeSpan[] CompleteRetryDelays { get; set; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private async Task<ClipUploadResult> UploadMultipartAsync(string filePath, long size, string contentType,
        string sessionToken, string? title, string? champion, int? durationSeconds, bool narrated,
        IProgress<ClipUploadProgress>? progress, Action<string>? onRemoteIdAssigned, CancellationToken ct)
    {
        var init = await InitAsync(size, contentType, sessionToken, title, champion, durationSeconds, narrated, ct)
            .ConfigureAwait(false);
        try
        {
            onRemoteIdAssigned?.Invoke(init.id);

            var expectedParts = (int)((size + init.part_size - 1) / init.part_size);
            if (init.part_size <= 0 || init.part_count != expectedParts)
            {
                throw new ClipUploadException("Server returned an unexpected response.");
            }

            progress?.Report(new ClipUploadProgress("uploading", 0, size));
            var parts = new List<PartDto>(init.part_count);
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(init.part_size, size));
            try
            {
                await using var file = OpenShared(filePath);
                long sent = 0;
                for (var n = 1; n <= init.part_count; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    var length = (int)Math.Min(init.part_size, size - sent);
                    file.Seek(sent, SeekOrigin.Begin);
                    await file.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);

                    var etag = await PutPartAsync(init.id, n, buffer, length, sessionToken, ct).ConfigureAwait(false);
                    parts.Add(new PartDto { part_number = n, etag = etag });
                    sent += length;
                    progress?.Report(new ClipUploadProgress("uploading", sent, size));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            progress?.Report(new ClipUploadProgress("finishing", size, size));
            var result = await CompleteAsync(init.id, parts, sessionToken, ct).ConfigureAwait(false);
            _logger.LogInformation("Clip uploaded (multipart, {Parts} parts): {Id}", parts.Count, result.Id);
            return result;
        }
        catch (Exception ex)
        {
            // Terminal failure or cancellation: drop the pending remote upload. Not bound to the
            // caller's (possibly already cancelled) token, but once that token fires (a stop or
            // shutdown) the abort gets only a short budget so it cannot hold up the shutdown
            // drain. The caller's onRemoteIdAssigned cleanup queue still holds the id.
            using var budget = new CancellationBudget(ct, AbortBudgetAfterCancel);
            var deleted = await DeleteAsync(init.id, sessionToken, budget.Token).ConfigureAwait(false);
            _logger.LogInformation(ex, "Multipart upload {Id} abandoned (remote delete {Deleted})", init.id, deleted);
            throw;
        }
    }

    private async Task<InitResponseDto> InitAsync(long size, string contentType, string sessionToken,
        string? title, string? champion, int? durationSeconds, bool narrated, CancellationToken ct)
    {
        var query = MetadataQuery(title, champion, durationSeconds);
        query.Add($"size={size.ToString(CultureInfo.InvariantCulture)}");
        query.Add($"narrated={(narrated ? 1 : 0)}");

        using var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = 0;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/clips/uploads?{string.Join("&", query)}")
        {
            Content = content,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        using var timeout = new CancellationTokenSource(InitTimeout);
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
            throw new ClipUploadException(TimeoutMessage, ex, retryable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogError(ex, "Multipart init failed to send");
            throw new ClipUploadException(NetworkMessage, ex, retryable: true);
        }

        using (res)
        {
            // A proxy older than 3.14 has no multipart routes: POST /clips/uploads falls through
            // to its 405 method_not_allowed guard (404 is kept for any other old shape).
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                throw new ClipUploadException(UnavailableMessage);
            await ThrowIfNotOkAsync(res, retryableContext: true, linked.Token).ConfigureAwait(false);
            InitResponseDto? body;
            try
            {
                body = await res.Content.ReadFromJsonAsync<InitResponseDto>(cancellationToken: linked.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ClipUploadException("Server returned an unexpected response.", ex);
            }
            if (body is null || string.IsNullOrWhiteSpace(body.id))
                throw new ClipUploadException("Server returned an unexpected response.");
            if (body.part_count <= 0 || body.part_size <= 0 || body.part_size > MaxPartBytes)
            {
                // The upload exists remotely: drop it before failing.
                await DeleteAsync(body.id, sessionToken, CancellationToken.None).ConfigureAwait(false);
                throw new ClipUploadException("Server returned an unexpected response.");
            }
            return body;
        }
    }

    private async Task<string> PutPartAsync(string id, int partNumber, byte[] buffer, int length,
        string sessionToken, CancellationToken ct)
    {
        var url = $"{_baseUrl}/clips/{Uri.EscapeDataString(id)}/parts/{partNumber.ToString(CultureInfo.InvariantCulture)}";
        for (var attempt = 1; ; attempt++)
        {
            var canRetry = attempt <= PartRetryDelays.Length;
            try
            {
                using var content = new ByteArrayContent(buffer, 0, length);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentLength = length;
                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

                using var timeout = new CancellationTokenSource(PartTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                using var res = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadFromJsonAsync<PartDto>(cancellationToken: linked.Token)
                        .ConfigureAwait(false);
                    if (body is null || string.IsNullOrWhiteSpace(body.etag))
                        throw new ClipUploadException("Server returned an unexpected response.");
                    return body.etag;
                }

                var status = (int)res.StatusCode;
                if (canRetry && (status >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests))
                {
                    _logger.LogWarning("Part {Part} of {Id} returned {Status}; retrying", partNumber, id, status);
                }
                else
                {
                    // 400 (size mismatch / bad part) and 404 (upload gone) abort, as does
                    // anything else that retrying cannot fix.
                    if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                        throw new ClipUploadException(UnavailableMessage);
                    await ThrowIfNotOkAsync(res, retryableContext: false, linked.Token).ConfigureAwait(false);
                    throw new ClipUploadException(UnavailableMessage);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException
                                       or JsonException)
            {
                if (!canRetry)
                    throw new ClipUploadException(ex is OperationCanceledException ? TimeoutMessage : NetworkMessage, ex);
                _logger.LogWarning(ex, "Part {Part} of {Id} failed; retrying", partNumber, id);
            }

            await Task.Delay(PartRetryDelays[attempt - 1], ct).ConfigureAwait(false);
        }
    }

    private async Task<ClipUploadResult> CompleteAsync(string id, IReadOnlyList<PartDto> parts, string sessionToken,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new CompleteBodyDto { parts = parts.ToList() });
        var url = $"{_baseUrl}/clips/{Uri.EscapeDataString(id)}/complete";
        for (var attempt = 1; ; attempt++)
        {
            var canRetry = attempt <= CompleteRetryDelays.Length;
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
                using var timeout = new CancellationTokenSource(CompleteTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                using var res = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) return await ReadResultAsync(res, linked.Token).ConfigureAwait(false);
                if (!(canRetry && (int)res.StatusCode >= 500))
                {
                    await ThrowIfNotOkAsync(res, retryableContext: false, linked.Token).ConfigureAwait(false);
                }
                _logger.LogWarning("Complete of {Id} returned {Status}; retrying", id, (int)res.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                if (!canRetry)
                    throw new ClipUploadException(ex is OperationCanceledException ? TimeoutMessage : NetworkMessage, ex);
                _logger.LogWarning(ex, "Complete of {Id} failed; retrying", id);
            }

            await Task.Delay(CompleteRetryDelays[attempt - 1], ct).ConfigureAwait(false);
        }
    }

    private sealed class InitResponseDto
    {
        public string id { get; set; } = "";
        public long part_size { get; set; }
        public int part_count { get; set; }
        public long expires_at { get; set; }
    }

    private sealed class PartDto
    {
        public int part_number { get; set; }
        public string etag { get; set; } = "";
    }

    private sealed class CompleteBodyDto
    {
        public List<PartDto> parts { get; set; } = new();
    }
}

/// <summary>Read-only pass-through stream that reports the cumulative bytes read.</summary>
internal sealed class ProgressReadStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onProgress;
    private long _read;

    public ProgressReadStream(Stream inner, Action<long> onProgress)
    {
        _inner = inner;
        _onProgress = onProgress;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int n)
    {
        if (n > 0)
        {
            _read += n;
            _onProgress(_read);
        }
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

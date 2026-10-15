#nullable enable

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Revu.Core.Services;

/// <summary>
/// R2 multipart sharing for clips above 95 MiB (C4 4.1 to 4.4): init, parts of the
/// server-chosen size (several in flight at once), complete. Any terminal failure or
/// cancellation DELETEs the remote upload with its own 10 s timeout (2 s once the caller's
/// token has fired).
/// </summary>
public sealed partial class ClipUploadService
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PartTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Parts in flight at once, each on its own connection with its own pooled buffer. One
    /// part at a time leaves a fast uplink idle while R2 stores each part: on a line that
    /// carried about 220 Mbps with four streams, one stream topped out near 65 Mbps. The
    /// proxy counts each part attempt on its own, so this changes no limit there.
    /// </summary>
    internal int PartConcurrency { get; set; } = 4;

    /// <summary>How long the remote abort may take once the caller's token has fired.</summary>
    internal static readonly TimeSpan AbortBudgetAfterCancel = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Largest part size the client accepts from init (the proxy sends 16 MiB). A part is
    /// held in one pooled buffer, so an absurd size is refused rather than allocated.
    /// </summary>
    internal const long MaxPartBytes = 256L * 1024 * 1024;

    /// <summary>Delays between the 3 attempts of complete (tests shorten these).</summary>
    internal TimeSpan[] CompleteRetryDelays { get; set; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private async Task<ClipUploadResult> UploadMultipartAsync(string filePath, long size, string contentType,
        string sessionToken, string? title, string? champion, int? durationSeconds, bool narrated,
        IProgress<ClipUploadProgress>? progress, Action<string>? onRemoteIdAssigned, CancellationToken ct)
    {
        var init = await InitAsync(size, contentType, sessionToken, title, champion, durationSeconds, narrated, ct)
            .ConfigureAwait(false);
        return await UploadPartsAndCompleteAsync(ClipsRouteBase, init.id, init.part_size, init.part_count, filePath,
            size, sessionToken, progress, onRemoteIdAssigned, "Clip", ct).ConfigureAwait(false);
    }

    /// <summary>Route base of the clip multipart routes (<c>/clips/{id}/parts/{n}</c>, <c>/clips/{id}/complete</c>).</summary>
    internal const string ClipsRouteBase = "clips";

    /// <summary>
    /// The part uploads and complete of an initialized multipart upload under
    /// <paramref name="routeBase"/> (<c>clips</c> or <c>vod-shares</c>). Up to
    /// <see cref="PartConcurrency"/> parts are in flight; the first part that fails for good
    /// stops the others and is the error reported. With <paramref name="written"/> the file is
    /// still being written (see <see cref="PutPartsAsync"/>). Any terminal failure or
    /// cancellation DELETEs the remote upload (kind-agnostic <c>DELETE /clips/{id}</c>).
    /// </summary>
    private async Task<ClipUploadResult> UploadPartsAndCompleteAsync(string routeBase, string id, long partSize,
        int partCount, string filePath, long size, string sessionToken, IProgress<ClipUploadProgress>? progress,
        Action<string>? onRemoteIdAssigned, string what, CancellationToken ct, Task<bool>? written = null)
    {
        try
        {
            onRemoteIdAssigned?.Invoke(id);

            var expectedParts = partSize <= 0 ? -1 : (int)((size + partSize - 1) / partSize);
            if (partSize <= 0 || partCount != expectedParts)
            {
                throw new ClipUploadException("Server returned an unexpected response.");
            }

            progress?.Report(new ClipUploadProgress("uploading", 0, size));
            var etags = await PutPartsAsync(routeBase, id, partSize, partCount, filePath, size, sessionToken, progress, ct,
                written).ConfigureAwait(false);
            var parts = etags.Select((etag, i) => new PartDto { part_number = i + 1, etag = etag }).ToList();

            progress?.Report(new ClipUploadProgress("finishing", size, size));
            var result = await CompleteAsync(routeBase, id, parts, sessionToken, ct).ConfigureAwait(false);
            _logger.LogInformation("{What} uploaded (multipart, {Parts} parts): {Id}", what, parts.Count, result.Id);
            return result;
        }
        catch (Exception ex)
        {
            // Terminal failure or cancellation: drop the pending remote upload. Not bound to the
            // caller's (possibly already cancelled) token, but once that token fires (a stop or
            // shutdown) the abort gets only a short budget so it cannot hold up the shutdown
            // drain. The caller's onRemoteIdAssigned cleanup queue still holds the id.
            using var budget = new CancellationBudget(ct, AbortBudgetAfterCancel);
            var deleted = await DeleteAsync(id, sessionToken, budget.Token).ConfigureAwait(false);
            _logger.LogInformation(ex, "Multipart upload {Id} abandoned (remote delete {Deleted})", id, deleted);
            throw;
        }
    }

    /// <summary>
    /// Every part of the file, up to <see cref="PartConcurrency"/> at once, each pump taking
    /// the next part. Returns the etags in part order. Progress counts finished parts and never
    /// goes backwards. The first failure cancels the other pumps and is rethrown as it was
    /// (their cancellations are a consequence, not the cause).
    /// <para>
    /// With <paramref name="written"/> the encoder is still writing the file. Its mp4 muxer
    /// (no +faststart) never changes a byte past the header once written, only the mdat size
    /// near the start when it finishes, so a middle part goes up as soon as the file has grown
    /// <see cref="GrowGuardBytes"/> past it. Part 1 (the header) and the last part wait until
    /// <paramref name="written"/> is true: the file is final at exactly <paramref name="size"/>
    /// bytes. False, a fault or a cancellation abandons the upload
    /// (<see cref="VodCopyAbandonedException"/>).
    /// </para>
    /// </summary>
    private async Task<string[]> PutPartsAsync(string routeBase, string id, long partSize, int partCount, string filePath,
        long size, string sessionToken, IProgress<ClipUploadProgress>? progress, CancellationToken ct,
        Task<bool>? written = null)
    {
        var etags = new string[partCount];
        var order = PartOrder(partCount, growing: written is not null);
        var gate = new object();
        var next = -1;
        long sent = 0;
        ExceptionDispatchInfo? first = null;
        // A finished file opens now (and must exist); a growing one opens once the encoder has created it.
        SafeFileHandle? file = written is null
            ? File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous)
            : null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        SafeFileHandle? TryOpen()
        {
            lock (gate)
            {
                if (file is not null) return file;
                try
                {
                    // The encoder holds the file open for writing, so reading must share write access.
                    file = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Not created yet.
                }
                return file;
            }
        }

        async Task WaitForPartAsync(int n)
        {
            var end = Math.Min(n * partSize, size);
            var middle = n > 1 && n < partCount;
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (written!.IsCompleted)
                {
                    if (written.IsCompletedSuccessfully && written.Result && TryOpen() is not null) return;
                    throw new VodCopyAbandonedException();
                }
                if (middle && TryOpen() is { } handle && RandomAccess.GetLength(handle) >= end + GrowGuardBytes) return;
                await Task.WhenAny(written, Task.Delay(GrowPollInterval, stop.Token)).ConfigureAwait(false);
            }
        }

        async Task PumpAsync()
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(partSize, size));
            try
            {
                for (var i = Interlocked.Increment(ref next); i < order.Length; i = Interlocked.Increment(ref next))
                {
                    var n = order[i];
                    stop.Token.ThrowIfCancellationRequested();
                    if (written is not null) await WaitForPartAsync(n).ConfigureAwait(false);
                    var offset = (n - 1) * partSize;
                    var length = (int)Math.Min(partSize, size - offset);
                    await ReadAtAsync(file!, buffer.AsMemory(0, length), offset, stop.Token).ConfigureAwait(false);
                    etags[n - 1] = await PutPartAsync(routeBase, id, n, buffer, length, sessionToken, stop.Token)
                        .ConfigureAwait(false);
                    lock (gate)
                    {
                        sent += length;
                        progress?.Report(new ClipUploadProgress("uploading", sent, size));
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate) first ??= ExceptionDispatchInfo.Capture(ex);
                stop.Cancel();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        try
        {
            var pumps = Enumerable.Range(0, Math.Clamp(PartConcurrency, 1, partCount)).Select(_ => PumpAsync()).ToArray();
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) file?.Dispose();
        }
        first?.Throw();
        return etags;
    }

    /// <summary>
    /// Upload order: 1..N for a finished file. For a growing one the parts the encoder can no
    /// longer change go first (2..N-1, as it passes them), then part 1 (the header it patches
    /// last) and part N (the end of the file).
    /// </summary>
    internal static int[] PartOrder(int partCount, bool growing)
    {
        if (!growing || partCount <= 2) return Enumerable.Range(1, partCount).ToArray();
        return [.. Enumerable.Range(2, partCount - 2), 1, partCount];
    }

    /// <summary>How far past a part's end the growing file must be before the part is read (writes in flight).</summary>
    internal const long GrowGuardBytes = 4L * 1024 * 1024;

    /// <summary>How often a growing upload checks the file's length (tests shorten it).</summary>
    internal TimeSpan GrowPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Fill <paramref name="buffer"/> from <paramref name="offset"/> (positional, safe to run concurrently).</summary>
    private static async Task ReadAtAsync(SafeFileHandle file, Memory<byte> buffer, long offset, CancellationToken ct)
    {
        while (!buffer.IsEmpty)
        {
            var read = await RandomAccess.ReadAsync(file, buffer, offset, ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The prepared file ended early.");
            buffer = buffer[read..];
            offset += read;
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

    private async Task<string> PutPartAsync(string routeBase, string id, int partNumber, byte[] buffer, int length,
        string sessionToken, CancellationToken ct)
    {
        var url = $"{_baseUrl}/{routeBase}/{Uri.EscapeDataString(id)}/parts/{partNumber.ToString(CultureInfo.InvariantCulture)}";
        // Clips: 4 attempts of any kind. Full games run for hours, so a short outage must not
        // throw the whole upload away: attempts that reach the proxy keep the clip limit, and
        // connection failures retry with backoff for up to VodPartOutageWindow.
        var retry = routeBase == VodRouteBase ? VodRetry() : null;
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? delay = attempt <= PartRetryDelays.Length ? PartRetryDelays[attempt - 1] : null;
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
                if (retry is not null) delay = retry.Server();
                if (delay is not null && (status >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests))
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
                if (retry is not null) delay = retry.After(ex);
                if (delay is null)
                    throw new ClipUploadException(ex is OperationCanceledException ? TimeoutMessage : NetworkMessage, ex);
                _logger.LogWarning(ex, "Part {Part} of {Id} failed; retrying", partNumber, id);
            }

            await Task.Delay(delay.Value, ct).ConfigureAwait(false);
        }
    }

    private async Task<ClipUploadResult> CompleteAsync(string routeBase, string id, IReadOnlyList<PartDto> parts,
        string sessionToken, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new CompleteBodyDto { parts = parts.ToList() });
        var url = $"{_baseUrl}/{routeBase}/{Uri.EscapeDataString(id)}/complete";
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

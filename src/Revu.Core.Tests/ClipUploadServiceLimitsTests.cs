using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The 2 GiB share cap and the init response guards, against real files on disk. The
/// 2 GiB files are NTFS sparse (no disk space is allocated); on a volume without sparse
/// support the size tests return early instead of writing gigabytes.
/// </summary>
public sealed class ClipUploadServiceLimitsTests : IDisposable
{
    private const long TwoGiB = 2147483648L;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "upload-limits-" + Guid.NewGuid().ToString("N"));

    public ClipUploadServiceLimitsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Method, string Path, string Query)> Requests { get; } = new();
        public required Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            lock (Requests) Requests.Add((req.Method.Method, req.RequestUri!.AbsolutePath, req.RequestUri.Query));
            return Task.FromResult(Respond(req));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static (ClipUploadService Service, Recorder Proxy) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var proxy = new Recorder { Respond = respond };
        return (new ClipUploadService(new HttpClient(proxy) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<ClipUploadService>.Instance, "https://proxy.test"), proxy);
    }

    [Fact]
    public async Task AFileOverTwoGibibytes_IsRefusedBeforeAnyRequest()
    {
        var file = SparseFile.TryCreate(Path.Combine(_dir, "huge.mp4"), TwoGiB + 1);
        if (file is null) return; // volume without sparse files
        var (service, proxy) = Create(_ => Json(HttpStatusCode.Created, new { }));

        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(file, "tok", durationSeconds: 600));

        Assert.Equal("Clip is too large to share (2 GB max).", ex.Message);
        Assert.False(ex.Retryable);
        Assert.Empty(proxy.Requests);
    }

    [Fact]
    public async Task ExactlyTwoGibibytes_IsAllowed_AndGoesMultipart()
    {
        var file = SparseFile.TryCreate(Path.Combine(_dir, "edge.mp4"), TwoGiB);
        if (file is null) return;
        // An old proxy without multipart: init answers 404, so no part is ever read.
        var (service, proxy) = Create(_ => Json(HttpStatusCode.NotFound, new { error = "not_found" }));

        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(file, "tok", durationSeconds: 600));

        Assert.Equal("Sharing is temporarily unavailable. Try again in a moment.", ex.Message);
        var init = Assert.Single(proxy.Requests);
        Assert.Equal(("POST", "/clips/uploads"), (init.Method, init.Path));
        Assert.Contains($"size={TwoGiB}", init.Query);
    }

    [Theory]
    [InlineData(0L, 7)]
    [InlineData(1024L * 1024 * 1024, 1)]
    [InlineData(16L * 1024 * 1024, 0)]
    public async Task AnUnusableInitResponse_IsRefused_AndTheRemoteUploadDropped(long partSize, int partCount)
    {
        var path = Path.Combine(_dir, "big.mp4");
        using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(100L * 1024 * 1024);
        var (service, proxy) = Create(req => req.Method == HttpMethod.Delete
            ? Json(HttpStatusCode.OK, new { ok = true })
            : Json(HttpStatusCode.Created, new { id = "mp9", part_size = partSize, part_count = partCount, expires_at = 1 }));

        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(path, "tok", durationSeconds: 300));

        Assert.Equal("Server returned an unexpected response.", ex.Message);
        Assert.Contains(proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/mp9");
        Assert.DoesNotContain(proxy.Requests, r => r.Path.Contains("/parts/"));
    }
}

/// <summary>Creates NTFS sparse files of any length without allocating disk space.</summary>
internal static class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
        uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    /// <summary>The path of a sparse file of <paramref name="length"/> bytes, or null when unsupported.</summary>
    public static string? TryCreate(string path, long length)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            if (!DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                fs.Dispose();
                File.Delete(path);
                return null;
            }
            fs.SetLength(length);
            return path;
        }
        catch (IOException)
        {
            try { File.Delete(path); } catch { }
            return null;
        }
    }
}

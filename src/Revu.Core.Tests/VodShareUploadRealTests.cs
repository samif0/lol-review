using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Opt-in (REVU_VOD_ENCODE_TEST=1 and REVU_VOD_ENCODE_SOURCE): a real encode of a recording cut
/// uploaded while ffmpeg writes it, to an in-process proxy that keeps every part. The parts put
/// back together must be the padded file byte for byte, and ffmpeg must read them as the video.
/// </summary>
public sealed class VodShareUploadRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Answers init, parts and complete like the proxy and keeps each part's bytes.</summary>
    private sealed class KeepingProxy : HttpMessageHandler
    {
        public ConcurrentDictionary<int, byte[]> Parts { get; } = new();
        public Func<int> EncodesDone { get; set; } = () => 0;
        public ConcurrentDictionary<int, int> SentDuringEncode { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path == "/vod-shares/uploads")
            {
                var size = long.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["size"]!);
                const long part = 16L * 1024 * 1024;
                return Json(HttpStatusCode.Created, new
                {
                    id = "real123", part_size = part, part_count = (int)((size + part - 1) / part), expires_at = 1,
                    share_expires_at = 2, url = "https://revu.lol/v/real123",
                });
            }
            if (req.Method == HttpMethod.Put && path.StartsWith("/vod-shares/real123/parts/", StringComparison.Ordinal))
            {
                var n = int.Parse(path.Split('/')[^1]);
                Parts[n] = await req.Content!.ReadAsByteArrayAsync(ct);
                SentDuringEncode[n] = EncodesDone();
                return Json(HttpStatusCode.OK, new { part_number = n, etag = "e" + n });
            }
            if (req.Method == HttpMethod.Post && path == "/vod-shares/real123/complete")
                return Json(HttpStatusCode.Created, new { id = "real123", url = "https://revu.lol/v/real123", expires_at = 2 });
            return Json(HttpStatusCode.OK, new { ok = true });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    [VodEncodeFact(needsSource: true)]
    public async Task ARealEncode_UploadsWhileFfmpegWrites_AndThePartsAreThePaddedFile()
    {
        var source = Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_SOURCE")!;
        var dir = Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_OUT");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "vodup-real");
        Directory.CreateDirectory(dir);
        var mixer = new NarrationMixer(NullLogger<NarrationMixer>.Instance);
        var encoder = new VodShareEncoder(mixer, NullLogger<VodShareEncoder>.Instance);
        var choice = await encoder.SelectAsync(CancellationToken.None);
        Assert.NotNull(choice);

        // A 120 s stream copy from 20:00, read-only on the source.
        var cut = Path.Combine(dir, "cut120.mp4");
        var copied = await FfmpegRunner.RunAsync(choice!.FfmpegPath,
            ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-ss", "1200", "-i", source, "-t", "120",
             "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-avoid_negative_ts", "make_zero", cut], 300, CancellationToken.None);
        Assert.True(copied.Succeeded, copied.StderrTail);
        var probe = await mixer.ProbeAsync(cut, CancellationToken.None);
        var fps = VodShareEncoderArgs.Fps(probe?.FrameRate);
        var duration = probe!.DurationSeconds!.Value;

        var output1 = Path.Combine(dir, "share-copy.mp4");
        var proxy = new KeepingProxy();
        var encoding = 1;
        proxy.EncodesDone = () => Volatile.Read(ref encoding);
        var service = new ClipUploadService(new HttpClient(proxy) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<ClipUploadService>.Instance, "https://proxy.test") { GrowPollInterval = TimeSpan.FromMilliseconds(50) };
        var declared = VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, fps, duration);
        var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var upload = service.UploadVodWhileWritingAsync(output1, declared, written.Task, "tok",
            new VodUploadRequest("1080p", (int)Math.Ceiling(duration), fps));
        var encoded = await encoder.EncodeAsync(cut, output1, VodShareQuality.P1080, fps, duration, null, CancellationToken.None);
        Volatile.Write(ref encoding, 0);
        var encodeSeconds = watch.Elapsed.TotalSeconds;
        Assert.True(encoded.SizeBytes <= declared, $"{encoded.SizeBytes} > {declared}");
        Assert.True(VodShareEncoder.TryPadTo(output1, declared));
        written.SetResult(true);
        var result = await upload;

        var during = proxy.SentDuringEncode.Count(kv => kv.Value == 1);
        output.WriteLine($"{VodShareEncoderArgs.Label(VodShareQuality.P1080, fps)} {encoded.Encoder}: {encoded.SizeBytes} bytes "
            + $"(reserved {declared}, padding {declared - encoded.SizeBytes}); encode {encodeSeconds:F1} s, all done "
            + $"{watch.Elapsed.TotalSeconds:F1} s; {during} of {proxy.Parts.Count} parts went up during the encode");
        Assert.Equal("real123", result.Id);

        var file = await File.ReadAllBytesAsync(output1);
        var assembled = Enumerable.Range(1, proxy.Parts.Count).SelectMany(n => proxy.Parts[n]).ToArray();
        Assert.Equal(file.Length, assembled.Length);
        Assert.True(file.AsSpan().SequenceEqual(assembled), "the parts are the padded file");

        var assembledPath = Path.Combine(dir, "assembled.mp4");
        await File.WriteAllBytesAsync(assembledPath, assembled);
        var back = await mixer.ProbeAsync(assembledPath, CancellationToken.None);
        Assert.NotNull(back?.DurationSeconds);
        Assert.True(Math.Abs(back!.DurationSeconds!.Value - duration) <= 2, $"{back.DurationSeconds} vs {duration}");
        Assert.Equal(fps, VodShareEncoderArgs.Fps(back.FrameRate));
    }
}

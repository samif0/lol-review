using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>Opt-in only: REVU_VOD_ENCODE_TEST=1 plus a local proxy (REVU_HLS_PROXY, a session token in REVU_HLS_PROXY_TOKEN).</summary>
public sealed class HlsProxyFactAttribute : FactAttribute
{
    public HlsProxyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_TEST") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REVU_HLS_PROXY"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REVU_HLS_PROXY_TOKEN")))
            Skip = "Needs REVU_VOD_ENCODE_TEST=1, REVU_HLS_PROXY (wrangler dev) and REVU_HLS_PROXY_TOKEN (a seeded sharer's session).";
    }
}

/// <summary>
/// The whole desktop side against a real proxy in wrangler dev (plan 7.3), never in CI: real HLS encodes of 60 s of
/// synthetic 1080p60 go up through <see cref="VodHlsUploadSession"/> while they encode, the share becomes playable,
/// ends, and its public playlist (behind a minted play token) lists every segment with ENDLIST.
/// </summary>
public sealed class VodHlsEndToEndRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int SourceSeconds = 60;

    private static readonly string Proxy = (Environment.GetEnvironmentVariable("REVU_HLS_PROXY") ?? "").TrimEnd('/');
    private static readonly string Token = Environment.GetEnvironmentVariable("REVU_HLS_PROXY_TOKEN") ?? "";
    private static readonly string Origin = Environment.GetEnvironmentVariable("REVU_HLS_ORIGIN") ?? "http://localhost:8788";

    [HlsProxyFact]
    public async Task ARealEncode_UploadsAsItEncodes_BecomesPlayable_AndPlaysFromThePublicPlaylist()
    {
        var dir = OutDir("hls-e2e");
        var encoder = NewEncoder();
        var choice = await encoder.SelectHlsAsync(CancellationToken.None);
        Assert.NotNull(choice);
        var source = await SourceAsync(choice!.FfmpegPath, dir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var service = await ServiceAsync(http);

        // Share: the init and the encode start together, and each segment goes up as it is written.
        var watch = Stopwatch.StartNew();
        var channel = Channel.CreateUnbounded<HlsItem>();
        var request = new VodHlsEncodeRequest(source, Path.Combine(dir, "segments"), VodShareQuality.P1080, 60, SourceSeconds);
        var encode = encoder.EncodeHlsAsync(request, channel.Writer, null, CancellationToken.None);
        var start = await service.StartVodHlsUploadAsync(Token, new VodUploadRequest("1080p", 60, SourceSeconds),
            VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 60, SourceSeconds));
        var linkMs = watch.Elapsed.TotalMilliseconds;
        Assert.NotNull(start.Session);
        await using var upload = start.Session!;
        double playableMs = -1;
        upload.Progress = s => { if (s.Playable && playableMs < 0) playableMs = watch.Elapsed.TotalMilliseconds; };
        upload.Start(CancellationToken.None);
        var feed = Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync()) upload.Add(item);
        });
        var encoded = await encode;
        channel.Writer.TryComplete();
        await feed;
        Assert.True(encoded.Complete, encoded.StopReason);
        upload.CompleteAdding();
        await upload.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        var end = await upload.EndAsync(upload.State.Acked, truncated: false, CancellationToken.None);
        output.WriteLine($"share {start.Started.Id}: link after {linkMs:F0} ms, playable after {playableMs:F0} ms, "
            + $"ended after {watch.Elapsed.TotalSeconds:F1} s: {end.Segments} segments, {end.SizeBytes} bytes, {end.MediaMs} ms");
        Assert.Equal(30, end.Segments);
        Assert.InRange(end.MediaMs, 59_900, 60_100);
        Assert.True(playableMs > 0);

        // A viewer: mint a play token, read the playlist, and let ffmpeg play it as an HLS client.
        var id = start.Started.Id;
        var playlistUrl = $"{Proxy}/vod-hls/{id}/{await MintAsync(http, id)}/index.m3u8";
        using (var res = await GetAsync(http, playlistUrl))
        {
            var playlist = await res.Content.ReadAsStringAsync();
            Assert.True(res.IsSuccessStatusCode, playlist);
            Assert.Equal(30, playlist.Split('\n').Count(l => l.EndsWith(".m4s", StringComparison.Ordinal)));
            Assert.Contains("#EXT-X-ENDLIST", playlist);
            Assert.Contains("#EXT-X-MAP:URI=\"init-0.mp4\"", playlist);
            Assert.DoesNotContain("public", res.Headers.CacheControl?.ToString() ?? "");
        }
        var client = await PlayAsync(choice.FfmpegPath, playlistUrl);
        output.WriteLine($"ffmpeg as the HLS client: exit {client.ExitCode} [{client.StderrTail.Trim()}]");
        Assert.True(client.Succeeded, client.StderrTail);
        Assert.Equal("", client.StderrTail.Trim());

        await StopAsync(http, upload, playlistUrl);
    }

    /// <summary>
    /// A share that pauses for a game and resumes at the edge twice, the second time at 720p: run 0 stops after a few
    /// segments and /pause shows on /vod-meta, run 1 resumes with the same settings and stops again, and run 2 finishes
    /// at 720p with its own init. The playlist joins the runs without a DISCONTINUITY and maps run 2's init where it
    /// starts, and every init and segment comes back byte for byte from the run that covers it.
    /// </summary>
    [HlsProxyFact]
    public async Task RunsResumedAtTheEdge_AreJoinedInThePlaylist_AndServedByteForByte()
    {
        var dir = OutDir("hls-e2e-runs");
        var encoder = NewEncoder();
        var choice = await encoder.SelectHlsAsync(CancellationToken.None);
        Assert.NotNull(choice);
        var source = await SourceAsync(choice!.FfmpegPath, dir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var service = await ServiceAsync(http);
        var start = await service.StartVodHlsUploadAsync(Token, new VodUploadRequest("1080p", 60, SourceSeconds),
            VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 60, SourceSeconds));
        Assert.NotNull(start.Session);
        await using var upload = start.Session!;
        upload.Start(CancellationToken.None);
        var id = start.Started.Id;
        var folder = Path.Combine(dir, "segments");

        var inits = new Dictionary<int, byte[]>();
        var run0 = await EncodeRunAsync(encoder, upload, inits, stopAfter: 5,
            new VodHlsEncodeRequest(source, folder, VodShareQuality.P1080, 60, SourceSeconds));
        await upload.PauseAsync("game", CancellationToken.None);
        using (var paused = await MetaAsync(http, id))
            Assert.Equal("game", paused.RootElement.GetProperty("paused_reason").GetString());

        var run1 = await EncodeRunAsync(encoder, upload, inits, stopAfter: 5,
            new VodHlsEncodeRequest(source, folder, VodShareQuality.P1080, 60, SourceSeconds, 1, run0.NextSeq, run0.EndSeconds));
        upload.Quality = VodShareQuality.P720;
        var run2 = await EncodeRunAsync(encoder, upload, inits, stopAfter: null,
            new VodHlsEncodeRequest(source, folder, VodShareQuality.P720, 60, SourceSeconds, 2, run1.NextSeq, run1.EndSeconds));
        output.WriteLine($"runs from 0, {run0.NextSeq} and {run1.NextSeq} ({run0.EndSeconds:F3} s, {run1.EndSeconds:F3} s); "
            + $"run 2 ends at {run2.NextSeq} ({run2.EndSeconds:F3} s); run 1 init same as run 0: {inits[0].SequenceEqual(inits[1])}");
        Assert.True(run2.Complete);
        Assert.Equal(30, run2.NextSeq);

        upload.CompleteAdding();
        await upload.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        var end = await upload.EndAsync(upload.State.Acked, truncated: false, CancellationToken.None);
        Assert.Equal(30, end.Segments);
        Assert.InRange(end.MediaMs, 59_900, 60_100);
        using (var ended = await MetaAsync(http, id))
        {
            var meta = ended.RootElement;
            Assert.Equal(("hls", true, false), (meta.GetProperty("format").GetString(), meta.GetProperty("ended").GetBoolean(),
                meta.GetProperty("truncated").GetBoolean()));
            Assert.Equal(JsonValueKind.Null, meta.GetProperty("paused_reason").ValueKind);
            Assert.InRange(meta.GetProperty("media_seconds").GetDouble(), 59.9, 60.1);
        }

        var play = await MintAsync(http, id);
        var playlistUrl = $"{Proxy}/vod-hls/{id}/{play}/index.m3u8";
        using var res = await GetAsync(http, playlistUrl);
        var playlist = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, playlist);
        output.WriteLine(playlist);
        var lines = playlist.Split('\n');
        // Runs join without a DISCONTINUITY (browser gate G3: hls.js leaves a hole in the buffer at a tagged resume),
        // and a new init is mapped right before its run's first segment, only where the init changes.
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", playlist);
        var mapped = Array.IndexOf(lines, "#EXT-X-MAP:URI=\"init-2.mp4\"");
        Assert.True(mapped > 0, "run 2's init is mapped");
        Assert.Equal($"{run1.NextSeq}.m4s", lines.Skip(mapped).First(l => l.EndsWith(".m4s", StringComparison.Ordinal)));
        Assert.Equal(!inits[0].SequenceEqual(inits[1]), playlist.Contains("init-1.mp4", StringComparison.Ordinal));
        Assert.Equal(30, lines.Count(l => l.EndsWith(".m4s", StringComparison.Ordinal)));
        Assert.Contains("#EXT-X-ENDLIST", playlist);

        foreach (var (k, bytes) in inits)
            Assert.Equal(bytes, await GetBytesAsync(http, $"{Proxy}/vod-hls/{id}/{play}/init-{k}.mp4"));
        for (var seq = 0; seq < 30; seq++)
        {
            var served = await GetBytesAsync(http, $"{Proxy}/vod-hls/{id}/{play}/{seq}.m4s");
            Assert.True(served.AsSpan().SequenceEqual(File.ReadAllBytes(Fmp4Splitter.SegmentPath(folder, seq))), $"segment {seq}");
        }

        // ffmpeg's HLS client skips a second moov ("Found duplicated MOOV Atom"), so it decodes run 2 with run 0's
        // parameter sets and logs errors past the 720p join; only a browser (gate G3) can judge that join. It must
        // still read the whole playlist.
        var client = await PlayAsync(choice.FfmpegPath, playlistUrl);
        output.WriteLine($"ffmpeg as the HLS client: exit {client.ExitCode}");
        Assert.True(client.Succeeded, client.StderrTail);

        await StopAsync(http, upload, playlistUrl);
    }

    private sealed record RunEdge(int NextSeq, double EndSeconds, bool Complete);

    /// <summary>
    /// One run, each item added to the upload as it is written; stopped (as a game start stops it) after
    /// <paramref name="stopAfter"/> segments. Returns the disk edge the next run resumes at.
    /// </summary>
    private static async Task<RunEdge> EncodeRunAsync(VodShareEncoder encoder, IVodHlsUpload upload, Dictionary<int, byte[]> inits,
        int? stopAfter, VodHlsEncodeRequest request)
    {
        using var stop = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<HlsItem>();
        var edge = new RunEdge(request.NextSeq, request.ResumeAtSeconds, false);
        var segments = 0;
        var feed = Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                upload.Add(item);
                if (item is HlsInit init) inits[init.Run] = init.Bytes;
                if (item is not HlsSegment segment) continue;
                edge = new RunEdge(segment.Seq + 1, segment.EndSeconds, false);
                if (++segments == stopAfter) stop.Cancel();
            }
        });
        var complete = false;
        try
        {
            complete = (await encoder.EncodeHlsAsync(request, channel.Writer, null, stop.Token)).Complete;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        channel.Writer.TryComplete();
        await feed;
        return edge with { Complete = complete };
    }

    private static string OutDir(string name)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_OUT") ?? Path.Combine(Path.GetTempPath(), "Revu.Core.Tests"),
            name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static VodShareEncoder NewEncoder() =>
        new(new NarrationMixer(NullLogger<NarrationMixer>.Instance), NullLogger<VodShareEncoder>.Instance);

    private static async Task<string> SourceAsync(string ffmpeg, string dir)
    {
        var source = Path.Combine(dir, "source.mp4");
        var made = await FfmpegRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=s=1920x1080:r=60:d={SourceSeconds}",
             "-f", "lavfi", "-i", $"sine=frequency=330:sample_rate=48000:d={SourceSeconds}", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac",
             "-shortest", source], 600, CancellationToken.None);
        Assert.True(made.Succeeded, made.StderrTail);
        return source;
    }

    private static async Task<ClipUploadService> ServiceAsync(HttpClient http)
    {
        var service = new ClipUploadService(http, NullLogger<ClipUploadService>.Instance, Proxy);
        var status = await service.GetVodShareStatusAsync(Token);
        Assert.True(status.Hls is { Enabled: true }, "the proxy must offer HLS to this account (VOD_HLS)");
        return service;
    }

    private static async Task<string> MintAsync(HttpClient http, string id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Proxy}/vod-play/{id}");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        using var res = await http.SendAsync(request);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<JsonDocument> MetaAsync(HttpClient http, string id)
    {
        using var res = await http.GetAsync($"{Proxy}/vod-meta/{id}");
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>A viewer's GET, from the site's origin.</summary>
    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        return await http.SendAsync(request);
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient http, string url)
    {
        using var res = await GetAsync(http, url);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {url}");
        return bytes;
    }

    private static Task<FfmpegRunResult> PlayAsync(string ffmpeg, string playlistUrl) =>
        FfmpegRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", playlistUrl, "-f", "null", "-"], 300,
            CancellationToken.None);

    /// <summary>Stop sharing removes it: viewers get 404 once the proxy's 2 s per-isolate cache has passed.</summary>
    private static async Task StopAsync(HttpClient http, IVodHlsUpload upload, string playlistUrl)
    {
        Assert.True(await upload.DeleteAsync(CancellationToken.None));
        var status = HttpStatusCode.OK;
        for (var i = 0; i < 20 && status != HttpStatusCode.NotFound; i++)
        {
            if (i > 0) await Task.Delay(250);
            using var res = await GetAsync(http, playlistUrl);
            status = res.StatusCode;
        }
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}

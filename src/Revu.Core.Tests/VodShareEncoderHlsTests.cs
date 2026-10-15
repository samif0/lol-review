using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;
using static Revu.Core.Tests.Fmp4TestMedia;

namespace Revu.Core.Tests;

/// <summary>
/// The HLS encode loop (plan 4.1, 6.1, 7.1) with ffmpeg faked as a stream of synthetic fragments: the self-tested
/// encoder choice and its cache, resuming at the edge (the same encoder once, then the next qualified one), the
/// no-progress limit, the run cap, and a short total that is logged rather than encoded again.
/// </summary>
public sealed class VodShareEncoderHlsTests : IDisposable
{
    private const string Bin = @"C:\app\ffmpeg.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "vodhls-" + Guid.NewGuid().ToString("N"));

    public VodShareEncoderHlsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>How one HLS run goes: fails (truncated, exit 1) after that many fragments, or hangs until cancelled.</summary>
    private sealed record RunScript(int? FailAfter = null, bool Hang = false);

    private sealed class FakeHlsFfmpeg
    {
        public HashSet<string> Listed { get; } = ["h264_nvenc", "h264_amf", "libx264"];
        public HashSet<string> ProbeFails { get; } = new();
        /// <summary>Encoders whose self-test makes 3 s fragments (an irregular GOP).</summary>
        public HashSet<string> SelfTestFails { get; } = new();
        public double SourceSeconds { get; set; } = 30;
        public Queue<RunScript> Scripts { get; } = new();
        public List<(string Kind, IReadOnlyList<string> Args)> Calls { get; } = new();

        public Task<VodEncoderRun> Run(string bin, IReadOnlyList<string> args, int timeout, Action<string>? onLine,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Contains("-encoders"))
            {
                foreach (var name in Listed) onLine?.Invoke($" V....D {name,-20} some encoder");
                return Task.FromResult(new VodEncoderRun(0, "", false));
            }
            var encoder = args[args.ToList().IndexOf("-c:v") + 1];
            lock (Calls) Calls.Add(("probe:" + encoder, args));
            return Task.FromResult(new VodEncoderRun(ProbeFails.Contains(encoder) ? 1 : 0, "", false));
        }

        public async Task<VodEncoderRun> RunBinary(string bin, IReadOnlyList<string> args, int timeout,
            Func<Stream, CancellationToken, Task> onStdout, CancellationToken ct)
        {
            var list = args.ToList();
            var encoder = list[list.IndexOf("-c:v") + 1];
            if (args.Contains("testsrc2=size=1920x1080:rate=60"))
            {
                lock (Calls) Calls.Add(("selftest:" + encoder, args));
                var data = SelfTestFails.Contains(encoder)
                    ? Concat(Init(), Fragment(new FragmentSpec { VideoFrames = 180 }).Moof, Fragment(new FragmentSpec { VideoFrames = 180 }).Mdat)
                    : Stream(2);
                return await FeedAsync(data, 0, onStdout, ct);
            }

            lock (Calls) Calls.Add(("run:" + encoder, args));
            var script = Scripts.Count > 0 ? Scripts.Dequeue() : new RunScript();
            var ss = list.IndexOf("-ss");
            var at = ss >= 0 ? double.Parse(list[ss + 1], CultureInfo.InvariantCulture) : 0;
            var remaining = SourceSeconds - at;
            var total = (int)Math.Ceiling(remaining / 2 - 1e-9);
            var lastFrames = (int)Math.Round((remaining - (total - 1) * 2) * 60);
            var count = Math.Min(script.FailAfter ?? total, total);
            var stream = count == 0 ? Init() : Stream(count, lastFrames: count == total ? lastFrames : 120);
            if (script.FailAfter is not null) stream = Concat(stream, Fragment(new FragmentSpec()).Moof[..20]);
            if (script.Hang)
            {
                await onStdout(new MemoryStream(stream), ct);
                await Task.Delay(Timeout.Infinite, ct);
            }
            return await FeedAsync(stream, script.FailAfter is null ? 0 : 1, onStdout, ct);
        }

        private static async Task<VodEncoderRun> FeedAsync(byte[] data, int exit, Func<Stream, CancellationToken, Task> onStdout,
            CancellationToken ct)
        {
            try
            {
                await onStdout(new MemoryStream(data), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new VodEncoderRun(-1, "", false, ex);
            }
            return new VodEncoderRun(exit, "", false);
        }

        public IEnumerable<string> Kinds(string prefix) => Calls.Select(c => c.Kind).Where(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static VodShareEncoder Encoder(FakeHlsFfmpeg fake, VodShareMemory? memory = null) =>
        new(null, NullLogger.Instance, _ => Task.FromResult<IReadOnlyList<string>>([Bin]), fake.Run, fake.RunBinary, memory)
        {
            DriverVersion = () => "Test GPU=1.0",
        };

    private VodHlsEncodeRequest Request(double duration = 30, int nextRun = 0, int nextSeq = 0, double at = 0, int maxRuns = 8) =>
        new(@"C:\rec\capture.mp4", _dir, VodShareQuality.P1080, 60, duration, nextRun, nextSeq, at, maxRuns);

    private static async Task<(VodHlsEncodeResult Result, List<HlsItem> Items)> EncodeAsync(VodShareEncoder encoder,
        VodHlsEncodeRequest request)
    {
        var channel = Channel.CreateUnbounded<HlsItem>();
        var result = await encoder.EncodeHlsAsync(request, channel.Writer, null, CancellationToken.None);
        channel.Writer.TryComplete();
        var items = new List<HlsItem>();
        await foreach (var item in channel.Reader.ReadAllAsync()) items.Add(item);
        return (result, items);
    }

    [Fact]
    public async Task AFailedRun_ResumesAtTheEdge_WithTheSameEncoderOnce_ThenTheNextQualifiedOne()
    {
        var fake = new FakeHlsFfmpeg();
        fake.Scripts.Enqueue(new RunScript(FailAfter: 5));
        fake.Scripts.Enqueue(new RunScript(FailAfter: 3));
        var (result, items) = await EncodeAsync(Encoder(fake), Request());

        Assert.Equal(new[] { "selftest:h264_nvenc", "run:h264_nvenc", "run:h264_nvenc", "selftest:h264_amf", "run:h264_amf" },
            fake.Calls.Select(c => c.Kind).Where(k => !k.StartsWith("probe:", StringComparison.Ordinal)));
        var runs = fake.Calls.Where(c => c.Kind.StartsWith("run:", StringComparison.Ordinal)).Select(c => string.Join(" ", c.Args)).ToList();
        Assert.DoesNotContain("-ss", runs[0]);
        Assert.Contains("-ss 10 -i C:\\rec\\capture.mp4 -ss 9.9 -i", runs[1]);
        Assert.Contains("-ss 16 -i C:\\rec\\capture.mp4 -ss 15.9 -i", runs[2]);

        var inits = items.OfType<HlsInit>().Select(i => (i.Run, i.FromSeq)).ToList();
        Assert.Equal(new[] { (0, 0), (1, 5), (2, 8) }, inits);
        var segments = items.OfType<HlsSegment>().ToList();
        Assert.Equal(Enumerable.Range(0, 15), segments.Select(s => s.Seq));
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2 }, segments.Select(s => s.Run));
        Assert.Equal(Enumerable.Range(0, 15).Select(i => 2.0 * i), segments.Select(s => s.StartSeconds));
        // Each init goes out right before its run's first segment.
        Assert.IsType<HlsInit>(items[items.FindIndex(i => i is HlsSegment { Seq: 5 }) - 1]);

        Assert.Equal(new VodHlsEncodeResult(true, 3, 15, 30, "h264_amf", null), result);
    }

    [Fact]
    public async Task TwoRunsWithoutANewSegment_EndAtTheEdge()
    {
        var fake = new FakeHlsFfmpeg();
        fake.Scripts.Enqueue(new RunScript(FailAfter: 5));
        fake.Scripts.Enqueue(new RunScript(FailAfter: 0));
        fake.Scripts.Enqueue(new RunScript(FailAfter: 0));
        var (result, items) = await EncodeAsync(Encoder(fake), Request());

        Assert.Equal(new VodHlsEncodeResult(false, 1, 5, 10, "h264_amf", "no_progress"), result);
        Assert.Equal(3, fake.Kinds("run:").Count());
        // A run that wrote no segment never announced an init.
        Assert.Single(items.OfType<HlsInit>());
    }

    [Fact]
    public async Task ARunThatWouldPassTheRunCap_IsNeverStarted()
    {
        var fake = new FakeHlsFfmpeg { SourceSeconds = 120 };
        fake.Scripts.Enqueue(new RunScript(FailAfter: 2));
        var (result, _) = await EncodeAsync(Encoder(fake), Request(nextRun: 7, nextSeq: 40, at: 80, duration: 120, maxRuns: 8));

        Assert.Equal(new VodHlsEncodeResult(false, 8, 42, 84, "h264_nvenc", "max_runs"), result);
        Assert.Single(fake.Kinds("run:"));
    }

    [Fact]
    public async Task AShortTotal_IsLoggedNotEncodedAgain()
    {
        // ffmpeg ends at 20 s although the probe said 60 s: the share ends there in one run.
        var fake = new FakeHlsFfmpeg { SourceSeconds = 20 };
        var (result, items) = await EncodeAsync(Encoder(fake), Request(duration: 60));

        Assert.Equal(new VodHlsEncodeResult(true, 1, 10, 20, "h264_nvenc", null), result);
        Assert.Single(fake.Kinds("run:"));
        Assert.Equal(10, items.OfType<HlsSegment>().Count());
    }

    [Fact]
    public async Task AResumeAfterAPause_ContinuesTheRunNumbersAndSeqs()
    {
        var fake = new FakeHlsFfmpeg { SourceSeconds = 40 };
        var (result, items) = await EncodeAsync(Encoder(fake), Request(duration: 40, nextRun: 2, nextSeq: 12, at: 24));

        Assert.Equal(new VodHlsEncodeResult(true, 3, 20, 40, "h264_nvenc", null), result);
        Assert.Equal((2, 12), items.OfType<HlsInit>().Select(i => (i.Run, i.FromSeq)).Single());
        Assert.Equal(24.0, items.OfType<HlsSegment>().First().StartSeconds);
    }

    [Fact]
    public async Task Selection_SkipsEncodersThatFailTheSelfTest_AndCachesEachResult()
    {
        var memory = new VodShareMemory(null);
        var fake = new FakeHlsFfmpeg();
        fake.SelfTestFails.Add("h264_nvenc");
        var choice = await Encoder(fake, memory).SelectHlsAsync(CancellationToken.None);

        Assert.Equal(new VodEncoderChoice(Bin, "h264_amf"), choice);
        Assert.Equal(new[] { "selftest:h264_nvenc", "selftest:h264_amf" }, fake.Kinds("selftest:"));

        // A new process with the same memory needs no self-test at all.
        var again = new FakeHlsFfmpeg();
        Assert.Equal(new VodEncoderChoice(Bin, "h264_amf"), await Encoder(again, memory).SelectHlsAsync(CancellationToken.None));
        Assert.Empty(again.Kinds("selftest:"));
        Assert.Empty(again.Kinds("probe:h264_nvenc"));
    }

    [Fact]
    public async Task ADriverUpdate_TestsAgain()
    {
        var memory = new VodShareMemory(null);
        var fake = new FakeHlsFfmpeg();
        fake.SelfTestFails.Add("h264_nvenc");
        await Encoder(fake, memory).SelectHlsAsync(CancellationToken.None);

        var updated = new FakeHlsFfmpeg();
        var encoder = Encoder(updated, memory);
        encoder.DriverVersion = () => "Test GPU=2.0";
        Assert.Equal(new VodEncoderChoice(Bin, "h264_nvenc"), await encoder.SelectHlsAsync(CancellationToken.None));
        Assert.Equal(new[] { "selftest:h264_nvenc" }, updated.Kinds("selftest:"));
    }

    [Fact]
    public async Task Prewarm_NeverPinsAWorsePair_WhenABetterOneThatPassedBeforeFailsItsProbeNow()
    {
        var memory = new VodShareMemory(null);
        await Encoder(new FakeHlsFfmpeg(), memory).SelectHlsAsync(CancellationToken.None);   // nvenc passes and is cached

        var busy = new FakeHlsFfmpeg();
        busy.ProbeFails.Add("h264_nvenc");                  // the GPU is busy while the sheet opens
        var encoder = Encoder(busy, memory);
        await encoder.PrewarmHlsAsync(CancellationToken.None);
        Assert.Contains("selftest:h264_amf", busy.Kinds("selftest:"));

        busy.ProbeFails.Clear();                            // at share time it works again
        Assert.Equal(new VodEncoderChoice(Bin, "h264_nvenc"), await encoder.SelectHlsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Prewarm_DoesNotCacheASelfTestFailure()
    {
        var memory = new VodShareMemory(null);
        var fake = new FakeHlsFfmpeg();
        fake.SelfTestFails.Add("h264_nvenc");
        await Encoder(fake, memory).PrewarmHlsAsync(CancellationToken.None);
        Assert.Null(memory.SelfTest($@"{Bin}|0|0|h264_nvenc|Test GPU=1.0"));
        Assert.True(memory.SelfTest($@"{Bin}|0|0|h264_amf|Test GPU=1.0"));
    }

    [Fact]
    public async Task NoEncoderPassing_FailsTheEncode()
    {
        var fake = new FakeHlsFfmpeg();
        fake.SelfTestFails.UnionWith(["h264_nvenc", "h264_amf", "libx264"]);
        var error = await Assert.ThrowsAsync<VodEncodeException>(() => EncodeAsync(Encoder(fake), Request()));
        Assert.Equal("encode_failed", error.Code);
    }

    [Fact]
    public async Task Cancellation_StopsTheRun_AndKeepsWhatWasAnnounced()
    {
        var fake = new FakeHlsFfmpeg();
        fake.Scripts.Enqueue(new RunScript(FailAfter: 4, Hang: true));
        var channel = Channel.CreateUnbounded<HlsItem>();
        using var cts = new CancellationTokenSource();
        var encode = Encoder(fake).EncodeHlsAsync(Request(), channel.Writer, null, cts.Token);
        var seen = 0;
        await foreach (var item in channel.Reader.ReadAllAsync())
            if (item is HlsSegment && ++seen == 4) break;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encode);
        Assert.True(File.Exists(Fmp4Splitter.SegmentPath(_dir, 3)));
    }

    [Fact]
    public async Task A720pShareTooLongToFit_IsRefusedBeforeAnyRun()
    {
        var fake = new FakeHlsFfmpeg();
        var request = Request(duration: 7300) with { Quality = VodShareQuality.P720 };
        var error = await Assert.ThrowsAsync<VodEncodeException>(() => EncodeAsync(Encoder(fake), request));
        Assert.Equal("too_long", error.Code);
        Assert.Empty(fake.Calls);
    }
}

using System.Threading.Channels;
using Revu.Core.Services;
using static Revu.Core.Tests.Fmp4TestMedia;

namespace Revu.Core.Tests;

/// <summary>
/// The HLS splitter (plan 4.1, 7.1) on synthetic fragmented MP4: box sizes, reads split anywhere, the segment rules
/// (IDR-first, at most 6.499 s), a resumed run's per-track tfdt shift and mfhd rewrite, and what it refuses.
/// </summary>
public sealed class Fmp4SplitterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "fmp4-" + Guid.NewGuid().ToString("N"));

    public Fmp4SplitterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed record Outcome(Fmp4Splitter Splitter, List<HlsItem> Items, Exception? Error);

    private async Task<Outcome> SplitAsync(byte[] data, int run = 0, int firstSeq = 0, double video = 0, double audio = 0,
        int chunk = int.MaxValue, int splitAt = -1, string? folder = null, long maxSegmentBytes = Fmp4Splitter.DefaultMaxSegmentBytes)
    {
        folder ??= _dir;
        Directory.CreateDirectory(folder);
        var channel = Channel.CreateUnbounded<HlsItem>();
        var splitter = new Fmp4Splitter(folder, run, firstSeq, video, audio, channel.Writer, maxSegmentBytes: maxSegmentBytes);
        Exception? error = null;
        try { await splitter.RunAsync(new ChunkedStream(data, chunk, splitAt), CancellationToken.None); }
        catch (Exception ex) { error = ex; }
        channel.Writer.TryComplete();
        var items = new List<HlsItem>();
        await foreach (var item in channel.Reader.ReadAllAsync()) items.Add(item);
        return new Outcome(splitter, items, error);
    }

    [Fact]
    public async Task ThreeFragments_BecomeThreeSegments_AfterOneInit()
    {
        var data = Stream(3, lastFrames: 90, mfra: true);
        var result = await SplitAsync(data, run: 0, firstSeq: 0);

        Assert.Null(result.Error);
        var init = Assert.IsType<HlsInit>(result.Items[0]);
        Assert.Equal((0, 0), (init.Run, init.FromSeq));
        Assert.Equal(Init(), init.Bytes);
        var segments = result.Items.Skip(1).Cast<HlsSegment>().ToList();
        Assert.Equal(new[] { 0, 1, 2 }, segments.Select(s => s.Seq));
        Assert.Equal(new[] { 2000, 2000, 1500 }, segments.Select(s => s.DurationMs));
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, segments.Select(s => s.StartSeconds));
        Assert.Equal(5.5, result.Splitter.EndSeconds);
        Assert.Equal((3, 3, false), (result.Splitter.Segments, result.Splitter.NextSeq, result.Splitter.Truncated));
        foreach (var s in segments)
        {
            Assert.Equal(Fmp4Splitter.SegmentPath(_dir, s.Seq), s.Path);
            Assert.Equal(new FileInfo(s.Path).Length, s.Bytes);
            Assert.Matches(@"seg-\d{5}\.m4s$", s.Path);
        }
        // A segment is the raw moof + mdat, no styp.
        var first = File.ReadAllBytes(segments[0].Path);
        Assert.Equal("moof", System.Text.Encoding.ASCII.GetString(first, 4, 4));
    }

    [Fact]
    public async Task ReadsSplitAtEveryOffset_GiveTheSameSegments()
    {
        // Small fragments (3 frames, 2 AAC frames) keep one split per byte cheap.
        var a = Fragment(new FragmentSpec { Sequence = 1, VideoFrames = 3, AudioTfdt = 0, AudioFrames = 2 });
        var b = Fragment(new FragmentSpec { Sequence = 2, VideoFrames = 3, VideoTfdt = 3 * Frame, AudioTfdt = 2 * AacFrame, AudioFrames = 2 });
        var data = Concat(Init(), a.Moof, a.Mdat, b.Moof, b.Mdat, Box("mfra"));
        var reference = await SplitAsync(data, folder: Path.Combine(_dir, "ref"));
        Assert.Null(reference.Error);
        var expected = reference.Items.OfType<HlsSegment>().Select(s => File.ReadAllBytes(s.Path)).ToList();

        for (var at = 1; at < data.Length; at += 1)
        {
            var folder = Path.Combine(_dir, "split");
            var split = await SplitAsync(data, splitAt: at, folder: folder);
            Assert.Null(split.Error);
            var segments = split.Items.OfType<HlsSegment>().ToList();
            Assert.Equal(expected.Count, segments.Count);
            for (var i = 0; i < segments.Count; i++) Assert.Equal(expected[i], File.ReadAllBytes(segments[i].Path));
        }

        var trickle = await SplitAsync(data, chunk: 1, folder: Path.Combine(_dir, "trickle"));
        Assert.Null(trickle.Error);
        Assert.Equal(expected.Count, trickle.Items.OfType<HlsSegment>().Count());
    }

    [Fact]
    public async Task LargeSizeFields_AreRead()
    {
        var (moof, mdat) = Fragment(new FragmentSpec { LargeMdat = true, AudioTfdt = 0 });
        var data = Concat(Init(), moof, mdat);
        var result = await SplitAsync(data);
        Assert.Null(result.Error);
        var segment = Assert.Single(result.Items.OfType<HlsSegment>());
        Assert.Equal(moof.Length + mdat.Length, segment.Bytes);
    }

    [Fact]
    public async Task VideoOnlyInput_Splits()
    {
        var result = await SplitAsync(Stream(2, audio: false));
        Assert.Null(result.Error);
        Assert.Equal(2, result.Items.OfType<HlsSegment>().Count());
    }

    [Fact]
    public async Task ResumedRun_ShiftsEachTrackOnItsOwnTimescale_AndContinuesTheSequence()
    {
        // A run resumed at 30 s: video from -ss 30, audio from -ss 29.9 (plan 4.1).
        var result = await SplitAsync(Stream(2), run: 3, firstSeq: 15, video: 30, audio: 29.9);
        Assert.Null(result.Error);
        var init = Assert.IsType<HlsInit>(result.Items[0]);
        Assert.Equal((3, 15), (init.Run, init.FromSeq));
        var segments = result.Items.OfType<HlsSegment>().ToList();
        Assert.Equal(new[] { 15, 16 }, segments.Select(s => s.Seq));
        Assert.All(segments, s => Assert.Equal(3, s.Run));
        Assert.Equal(new[] { 30.0, 32.0 }, segments.Select(s => s.StartSeconds));
        Assert.Equal(34.0, result.Splitter.EndSeconds);

        var (sequence, tfdt) = ReadSegment(File.ReadAllBytes(segments[1].Path));
        Assert.Equal(17u, sequence);
        Assert.Equal(30UL * VideoTimescale + 120 * Frame, tfdt[1]);
        Assert.Equal(2UL * AudioTimescale + 1_435_200, tfdt[2]);
    }

    [Fact]
    public async Task ThirtyTwoBitTfdt_IsShifted_AndAnOverflowIsRefused()
    {
        var (moof, mdat) = Fragment(new FragmentSpec { TfdtVersion = 0, VideoTfdt = 1000, AudioTfdt = 0 });
        var ok = await SplitAsync(Concat(Init(), moof, mdat), video: 1, audio: 0.9);
        Assert.Null(ok.Error);
        var (_, tfdt) = ReadSegment(File.ReadAllBytes(ok.Items.OfType<HlsSegment>().Single().Path));
        Assert.Equal(1000UL + VideoTimescale, tfdt[1]);
        Assert.Equal(43200UL, tfdt[2]);

        var (moof2, mdat2) = Fragment(new FragmentSpec { TfdtVersion = 0, VideoTfdt = uint.MaxValue - 10 });
        var overflow = await SplitAsync(Concat(Init(audio: false), moof2, mdat2), video: 1, folder: Path.Combine(_dir, "o"));
        Assert.IsType<HlsSegmentException>(overflow.Error);
        Assert.Contains("overflow", overflow.Error!.Message);
        Assert.Empty(overflow.Items);
    }

    [Theory]
    [InlineData(false, new[] { 9, 5 }, "non-sync")]
    [InlineData(true, new[] { 9, 1 }, "IDR")]
    [InlineData(true, new[] { 6, 7, 8, 1 }, "IDR")]
    public async Task AFragmentThatDoesNotStartOnAnIdrFrame_IsRefused(bool sync, int[] nals, string message)
    {
        var good = Fragment(new FragmentSpec { Sequence = 1 });
        var bad = Fragment(new FragmentSpec { Sequence = 2, VideoTfdt = 120 * Frame, FirstSync = sync, FirstNals = nals });
        var result = await SplitAsync(Concat(Init(audio: false), good.Moof, good.Mdat, bad.Moof, bad.Mdat));
        Assert.IsType<HlsSegmentException>(result.Error);
        Assert.Contains(message, result.Error!.Message);
        // The good segment before it was written and announced; the edge stays after it.
        Assert.Single(result.Items.OfType<HlsSegment>());
        Assert.Equal(2.0, result.Splitter.EndSeconds);
    }

    [Fact]
    public async Task IdrAfterSeiAndParameterSets_Passes_AndTwoByteNalLengthsAreRead()
    {
        var (moof, mdat) = Fragment(new FragmentSpec { FirstNals = [9, 6, 7, 8, 5] });
        Assert.Null((await SplitAsync(Concat(Init(audio: false), moof, mdat))).Error);

        var (moof2, mdat2) = Fragment(new FragmentSpec { NalLengthSize = 2, FirstNals = [9, 5] });
        var result = await SplitAsync(Concat(Init(audio: false, nalLengthSize: 2), moof2, mdat2), folder: Path.Combine(_dir, "two"));
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task AFragmentLongerThan6499Ms_IsRefused_AndOneAtTheLimitPasses()
    {
        // 6.5 s at 60 fps = 390 frames.
        var (moof, mdat) = Fragment(new FragmentSpec { VideoFrames = 390 });
        var result = await SplitAsync(Concat(Init(audio: false), moof, mdat));
        Assert.IsType<HlsSegmentException>(result.Error);
        Assert.Contains("6.500 s", result.Error!.Message);

        // 99,832 ticks at 15,360 is 6,499.48 ms: an EXTINF of 6,499 ms, the proxy's limit. One tick more rounds up.
        var (moof2, mdat2) = Fragment(new FragmentSpec { VideoFrames = 1, VideoFrameDuration = 99_832 });
        var ok = await SplitAsync(Concat(Init(audio: false), moof2, mdat2), folder: Path.Combine(_dir, "limit"));
        Assert.True(ok.Error is null, ok.Error?.Message);
        Assert.Equal(6499, ok.Items.OfType<HlsSegment>().Single().DurationMs);

        var (moof3, mdat3) = Fragment(new FragmentSpec { VideoFrames = 1, VideoFrameDuration = 99_833 });
        var over = await SplitAsync(Concat(Init(audio: false), moof3, mdat3), folder: Path.Combine(_dir, "over"));
        Assert.IsType<HlsSegmentException>(over.Error);
    }

    [Fact]
    public async Task TrailingMfra_IsIgnored_AndAStreamCutInsideABoxIsTruncated()
    {
        var withMfra = await SplitAsync(Stream(1, mfra: true));
        Assert.Null(withMfra.Error);
        Assert.False(withMfra.Splitter.Truncated);

        var data = Stream(2);
        var cut = await SplitAsync(data[..^10], folder: Path.Combine(_dir, "cut"));
        Assert.Null(cut.Error);
        Assert.True(cut.Splitter.Truncated);
        Assert.Single(cut.Items.OfType<HlsSegment>());
    }

    [Fact]
    public async Task NoSegment_AnnouncesNoInit()
    {
        var result = await SplitAsync(Init());
        Assert.Null(result.Error);
        Assert.Empty(result.Items);
        Assert.NotNull(result.Splitter.Init);
        Assert.Equal(0, result.Splitter.Segments);
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        var init = Init();
        var (moof, mdat) = Fragment(new FragmentSpec { AudioTfdt = 0 });
        return new TheoryData<string, byte[]>
        {
            { "no mvex", Concat(Init(mvex: false), moof, mdat) },
            { "before the init", Concat(moof, mdat) },
            { "mdat without a moof", Concat(init, mdat) },
            { "moof without its mdat", Concat(init, moof, moof, mdat) },
            { "unexpected 'sidx'", Concat(init, Box("sidx", Zeros(20)), moof, mdat) },
            { "to the end of the stream", Concat(init, U32(0), Ascii("mdat"), Zeros(10)) },
            { "unexpected 'text' track", Concat(Init(extraHandler: "text"), moof, mdat) },
            { "of 70000000 bytes", Concat(init, U32(70_000_000), Ascii("mdat")) },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task MalformedStreams_AreRefused(string message, byte[] data)
    {
        var result = await SplitAsync(data);
        var error = Assert.IsType<HlsSegmentException>(result.Error);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task ASegmentOverTheProxyLimit_IsRefused()
    {
        var result = await SplitAsync(Stream(1), maxSegmentBytes: 100);
        Assert.IsType<HlsSegmentException>(result.Error);
        Assert.Empty(result.Items);
    }

    /// <summary>A writer that stops the run, as a pause does, the moment it takes an init.</summary>
    private sealed class StopOnInitWriter(CancellationTokenSource stop) : ChannelWriter<HlsItem>
    {
        public List<HlsItem> Items { get; } = [];

        public override bool TryWrite(HlsItem item)
        {
            Items.Add(item);
            if (item is HlsInit) stop.Cancel();
            return true;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    [Fact]
    public async Task AStopRightAfterARunsInit_StillSendsItsFirstSegment()
    {
        // A run's init alone would make the next run start where this one does, which the proxy refuses (409).
        using var stop = new CancellationTokenSource();
        var writer = new StopOnInitWriter(stop);
        var splitter = new Fmp4Splitter(_dir, 1, 15, 30, 29.9, writer);

        var error = await Record.ExceptionAsync(() => splitter.RunAsync(new ChunkedStream(Stream(3, lastFrames: 90)), stop.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(2, writer.Items.Count);
        Assert.Equal((1, 15), (Assert.IsType<HlsInit>(writer.Items[0]).Run, ((HlsInit)writer.Items[0]).FromSeq));
        Assert.Equal(15, Assert.IsType<HlsSegment>(writer.Items[1]).Seq);
    }
}

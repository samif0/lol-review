#nullable enable

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Revu.Core.Services;

/// <summary>What the splitter hands the uploader, in order: a run's init right before its first segment, then segments.</summary>
public abstract record HlsItem(int Run);

/// <summary>Run <paramref name="Run"/>'s init (<c>ftyp</c>+<c>moov</c>); its first segment is <paramref name="FromSeq"/>.</summary>
public sealed record HlsInit(int Run, int FromSeq, byte[] Bytes) : HlsItem(Run);

/// <summary>
/// One finished segment file (one <c>moof</c>+<c>mdat</c>, one 2 s GOP). Start and End are on the share's
/// HLS timeline in seconds; DurationMs is the EXTINF (the video track's duration).
/// </summary>
public sealed record HlsSegment(int Seq, int DurationMs, long Bytes, string Path, int Run, double StartSeconds,
    double EndSeconds) : HlsItem(Run);

/// <summary>A fragment broke a segment rule (or the stream is not a usable fragmented MP4): an encoder fault.</summary>
public sealed class HlsSegmentException(string message) : Exception(message);

/// <summary>The tracks of an fMP4 init that matter to the splitter.</summary>
internal sealed record Fmp4Track(uint TrackId, string Handler, uint Timescale, uint DefaultDuration, uint DefaultFlags,
    int NalLengthSize);

/// <summary>
/// Splits the fragmented MP4 ffmpeg writes to stdout (<c>+frag_keyframe+empty_moov+default_base_moof+delay_moov</c>)
/// into HLS segments: one fragment is one segment (plan 4.1). Each fragment must start on a sync IDR frame and
/// last at most 6.499 s. A resumed run's timestamps are moved onto the share's timeline (video by
/// <c>videoShiftSeconds</c>, audio by <c>audioShiftSeconds</c>, each in its own track timescale) and its
/// <c>mfhd</c> sequence numbers continue from <c>firstSeq</c>. Segment files are written directly
/// (<c>seg-00012.m4s</c>, no rename) and announced only once closed. A trailing <c>mfra</c> is ignored;
/// anything else that breaks a rule throws <see cref="HlsSegmentException"/>. Pure: no ffmpeg, no logging.
/// </summary>
public sealed class Fmp4Splitter
{
    /// <summary>The largest top-level box read into memory.</summary>
    public const int MaxBoxBytes = 64 * 1024 * 1024;
    /// <summary>The longest fragment accepted (the proxy refuses an EXTINF above this).</summary>
    public const int MaxSegmentMs = 6499;
    /// <summary>The largest segment the proxy takes (16 MiB).</summary>
    public const long DefaultMaxSegmentBytes = 16L * 1024 * 1024;

    private readonly string _folder;
    private readonly int _run;
    private readonly int _firstSeq;
    private readonly double _videoShift;
    private readonly double _audioShift;
    private readonly long _maxSegmentBytes;
    private readonly ChannelWriter<HlsItem> _writer;
    private readonly IProgress<double>? _mediaSeconds;
    private Dictionary<uint, Fmp4Track>? _tracks;
    private bool _initSent;

    public Fmp4Splitter(string folder, int run, int firstSeq, double videoShiftSeconds, double audioShiftSeconds,
        ChannelWriter<HlsItem> writer, IProgress<double>? mediaSeconds = null, long maxSegmentBytes = DefaultMaxSegmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSeq);
        if (!double.IsFinite(videoShiftSeconds) || videoShiftSeconds < 0) throw new ArgumentOutOfRangeException(nameof(videoShiftSeconds));
        if (!double.IsFinite(audioShiftSeconds) || audioShiftSeconds < 0) throw new ArgumentOutOfRangeException(nameof(audioShiftSeconds));
        _folder = folder;
        _run = run;
        _firstSeq = firstSeq;
        _videoShift = videoShiftSeconds;
        _audioShift = audioShiftSeconds;
        _writer = writer;
        _mediaSeconds = mediaSeconds;
        _maxSegmentBytes = maxSegmentBytes;
        NextSeq = firstSeq;
        EndSeconds = videoShiftSeconds;
    }

    /// <summary>The run's init (<c>ftyp</c>+<c>moov</c>), once read.</summary>
    public byte[]? Init { get; private set; }
    /// <summary>Complete segments this run wrote.</summary>
    public int Segments => NextSeq - _firstSeq;
    /// <summary>The seq the next segment gets.</summary>
    public int NextSeq { get; private set; }
    /// <summary>Where the last complete segment ends on the share's timeline (the run's start before any).</summary>
    public double EndSeconds { get; private set; }
    /// <summary>The stream ended inside a box or after a <c>moof</c> without its <c>mdat</c>.</summary>
    public bool Truncated { get; private set; }

    /// <summary>The path of segment <paramref name="seq"/> in <paramref name="folder"/>.</summary>
    public static string SegmentPath(string folder, int seq) =>
        Path.Combine(folder, "seg-" + seq.ToString("D5", CultureInfo.InvariantCulture) + ".m4s");

    /// <summary>Read <paramref name="stdout"/> to its end, writing and announcing every complete segment.</summary>
    public async Task RunAsync(Stream stdout, CancellationToken ct)
    {
        byte[]? ftyp = null;
        byte[]? moof = null;
        long offset = 0;
        while (await ReadBoxAsync(stdout, ct).ConfigureAwait(false) is { } box)
        {
            var at = offset;
            offset += box.Bytes.Length;
            switch (box.Type)
            {
                case "ftyp":
                    if (ftyp is not null || Init is not null) throw new HlsSegmentException("a second ftyp box");
                    ftyp = box.Bytes;
                    break;
                case "moov":
                    if (ftyp is null || Init is not null) throw new HlsSegmentException("a moov box out of place");
                    var init = new byte[ftyp.Length + box.Bytes.Length];
                    ftyp.CopyTo(init, 0);
                    box.Bytes.CopyTo(init, ftyp.Length);
                    _tracks = ParseInit(init);
                    Init = init;
                    break;
                case "moof":
                    if (Init is null) throw new HlsSegmentException("a fragment before the init");
                    if (moof is not null) throw new HlsSegmentException("a moof without its mdat");
                    moof = box.Bytes;
                    break;
                case "mdat":
                    if (moof is null) throw new HlsSegmentException($"an mdat without a moof at byte {at}");
                    await EmitAsync(moof, box.Bytes, ct).ConfigureAwait(false);
                    moof = null;
                    break;
                case "mfra" or "free" or "skip":
                    if (moof is not null) throw new HlsSegmentException("a moof without its mdat");
                    break;
                default:
                    throw new HlsSegmentException($"an unexpected '{box.Type}' box at byte {at}");
            }
        }
        if (moof is not null) Truncated = true;
    }

    private readonly record struct Box(string Type, byte[] Bytes);

    private async Task<Box?> ReadBoxAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[16];
        var got = await ReadFullyAsync(stream, header.AsMemory(0, 8), ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 8) { Truncated = true; return null; }
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = Encoding.Latin1.GetString(header, 4, 4);
        var headerBytes = 8;
        if (size == 1)
        {
            if (await ReadFullyAsync(stream, header.AsMemory(8, 8), ct).ConfigureAwait(false) < 8) { Truncated = true; return null; }
            var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
            if (large > MaxBoxBytes) throw new HlsSegmentException($"a '{type}' box of {large} bytes");
            size = (long)large;
            headerBytes = 16;
        }
        else if (size == 0)
        {
            throw new HlsSegmentException($"a '{type}' box that runs to the end of the stream");
        }
        if (size < headerBytes) throw new HlsSegmentException($"a '{type}' box of {size} bytes");
        if (size > MaxBoxBytes) throw new HlsSegmentException($"a '{type}' box of {size} bytes");
        var bytes = new byte[size];
        header.AsSpan(0, headerBytes).CopyTo(bytes);
        var body = bytes.AsMemory(headerBytes);
        if (await ReadFullyAsync(stream, body, ct).ConfigureAwait(false) < body.Length) { Truncated = true; return null; }
        return new Box(type, bytes);
    }

    private static async Task<int> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private async Task EmitAsync(byte[] moof, byte[] mdat, CancellationToken ct)
    {
        var seq = NextSeq;
        var fragment = Rewrite(moof, mdat, _tracks!, seq + 1, _videoShift, _audioShift);
        if (moof.LongLength + mdat.LongLength > _maxSegmentBytes)
            throw new HlsSegmentException($"segment {seq} is {moof.LongLength + mdat.LongLength} bytes");

        var path = SegmentPath(_folder, seq);
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, useAsync: true))
        {
            await file.WriteAsync(moof, ct).ConfigureAwait(false);
            await file.WriteAsync(mdat, ct).ConfigureAwait(false);
        }

        var segmentCt = ct;
        if (!_initSent)
        {
            await _writer.WriteAsync(new HlsInit(_run, _firstSeq, Init!), ct).ConfigureAwait(false);
            _initSent = true;
            // A run's init never goes out without its first segment: the next run would start where this one does,
            // and the proxy refuses a run that doesn't start past the previous one.
            segmentCt = CancellationToken.None;
        }
        NextSeq = seq + 1;
        EndSeconds = fragment.EndSeconds;
        await _writer.WriteAsync(new HlsSegment(seq, fragment.DurationMs, moof.LongLength + mdat.LongLength, path, _run,
            fragment.StartSeconds, fragment.EndSeconds), segmentCt).ConfigureAwait(false);
        _mediaSeconds?.Report(fragment.EndSeconds);
    }

    // ── Parsing (synchronous, over whole boxes) ──────────────────────────────

    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "mvex", "moof", "traf", "edts", "dinf",
    };

    /// <summary>A box inside a buffer: where its header and its payload start, and where it ends.</summary>
    internal readonly record struct Span4(string Type, int Start, int PayloadStart, int End);

    /// <summary>The boxes in [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="buf"/>.</summary>
    internal static List<Span4> Children(byte[] buf, int start, int end)
    {
        var list = new List<Span4>();
        var o = start;
        while (o + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(o));
            var type = Encoding.Latin1.GetString(buf, o + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (o + 16 > end) throw new HlsSegmentException($"a truncated '{type}' box");
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(o + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - o;
            }
            if (size < header || o + size > end) throw new HlsSegmentException($"a truncated '{type}' box");
            list.Add(new Span4(type, o, o + header, (int)(o + size)));
            o += (int)size;
        }
        return list;
    }

    private static Span4? First(List<Span4> boxes, string type)
    {
        foreach (var b in boxes) if (b.Type == type) return b;
        return null;
    }

    private static Span4 Need(List<Span4> boxes, string type, string where) =>
        First(boxes, type) ?? throw new HlsSegmentException($"no {type} box in {where}");

    private static uint U32(byte[] buf, int at)
    {
        if (at < 0 || at + 4 > buf.Length) throw new HlsSegmentException("a box field past its end");
        return BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(at));
    }

    /// <summary>The init's tracks by id: one video track, at most one audio track, and an <c>mvex</c>.</summary>
    internal static Dictionary<uint, Fmp4Track> ParseInit(byte[] init)
    {
        var top = Children(init, 0, init.Length);
        var moov = Need(top, "moov", "the init");
        var children = Children(init, moov.PayloadStart, moov.End);
        var mvex = Need(children, "mvex", "the moov");
        var trex = new Dictionary<uint, (uint Duration, uint Flags)>();
        foreach (var t in Children(init, mvex.PayloadStart, mvex.End))
        {
            if (t.Type != "trex") continue;
            // version/flags 4, track_ID 4, default_sample_description_index 4, duration 4, size 4, flags 4
            trex[U32(init, t.PayloadStart + 4)] = (U32(init, t.PayloadStart + 12), U32(init, t.PayloadStart + 20));
        }

        var tracks = new Dictionary<uint, Fmp4Track>();
        foreach (var trak in children)
        {
            if (trak.Type != "trak") continue;
            var inner = Children(init, trak.PayloadStart, trak.End);
            var tkhd = Need(inner, "tkhd", "a trak");
            var tkhdVersion = init[tkhd.PayloadStart];
            var trackId = U32(init, tkhd.PayloadStart + 4 + (tkhdVersion == 1 ? 16 : 8));
            var mdia = Need(inner, "mdia", "a trak");
            var mdiaChildren = Children(init, mdia.PayloadStart, mdia.End);
            var mdhd = Need(mdiaChildren, "mdhd", "an mdia");
            var mdhdVersion = init[mdhd.PayloadStart];
            var timescale = U32(init, mdhd.PayloadStart + 4 + (mdhdVersion == 1 ? 16 : 8));
            var hdlr = Need(mdiaChildren, "hdlr", "an mdia");
            if (hdlr.PayloadStart + 12 > hdlr.End) throw new HlsSegmentException("a truncated hdlr box");
            var handler = Encoding.Latin1.GetString(init, hdlr.PayloadStart + 8, 4);
            if (timescale == 0) throw new HlsSegmentException($"track {trackId} has no timescale");
            if (handler is not ("vide" or "soun")) throw new HlsSegmentException($"an unexpected '{handler}' track");
            var defaults = trex.TryGetValue(trackId, out var d) ? d : (0u, 0u);
            var nal = handler == "vide" ? NalLengthSize(init, mdiaChildren) : 0;
            tracks[trackId] = new Fmp4Track(trackId, handler, timescale, defaults.Item1, defaults.Item2, nal);
        }
        if (tracks.Values.Count(t => t.Handler == "vide") != 1) throw new HlsSegmentException("the init needs exactly one video track");
        if (tracks.Values.Count(t => t.Handler == "soun") > 1) throw new HlsSegmentException("the init has more than one audio track");
        return tracks;
    }

    /// <summary>The AVCC NAL length size from <c>stsd/avc1/avcC</c>; 4 when it can't be found.</summary>
    private static int NalLengthSize(byte[] init, List<Span4> mdiaChildren)
    {
        var minf = First(mdiaChildren, "minf");
        if (minf is null) return 4;
        var stbl = First(Children(init, minf.Value.PayloadStart, minf.Value.End), "stbl");
        if (stbl is null) return 4;
        var stsd = First(Children(init, stbl.Value.PayloadStart, stbl.Value.End), "stsd");
        if (stsd is null) return 4;
        // stsd: version/flags 4, entry_count 4, then sample entries.
        var entries = Children(init, stsd.Value.PayloadStart + 8, stsd.Value.End);
        foreach (var entry in entries)
        {
            if (entry.Type is not ("avc1" or "avc3")) continue;
            // VisualSampleEntry fields take 78 bytes before the child boxes.
            if (entry.PayloadStart + 78 > entry.End) return 4;
            var avcC = First(Children(init, entry.PayloadStart + 78, entry.End), "avcC");
            if (avcC is null || avcC.Value.PayloadStart + 5 > avcC.Value.End) return 4;
            return (init[avcC.Value.PayloadStart + 4] & 3) + 1;
        }
        return 4;
    }

    /// <summary>A checked and shifted fragment: its place on the share's timeline and its EXTINF.</summary>
    internal readonly record struct Fragment(double StartSeconds, double EndSeconds, int DurationMs);

    /// <summary>
    /// Check one <c>moof</c>+<c>mdat</c> against the segment rules, then rewrite it in place: <c>mfhd</c> gets
    /// <paramref name="sequenceNumber"/>, each <c>tfdt</c> moves by its track's shift.
    /// </summary>
    internal static Fragment Rewrite(byte[] moof, byte[] mdat, IReadOnlyDictionary<uint, Fmp4Track> tracks,
        int sequenceNumber, double videoShiftSeconds, double audioShiftSeconds)
    {
        var boxes = Children(moof, 0, moof.Length);
        if (boxes.Count != 1 || boxes[0].Type != "moof") throw new HlsSegmentException("a fragment that is not one moof");
        var inner = Children(moof, boxes[0].PayloadStart, boxes[0].End);
        var mfhd = Need(inner, "mfhd", "a moof");
        var mdatHeader = Children(mdat, 0, mdat.Length)[0].PayloadStart;

        Fragment? video = null;
        var trafEnd = 0L;   // where the previous track run's data ended (moof-relative)
        var seenVideo = false;
        foreach (var traf in inner)
        {
            if (traf.Type != "traf") continue;
            var parts = Children(moof, traf.PayloadStart, traf.End);
            var tfhd = Need(parts, "tfhd", "a traf");
            var tfhdFlags = U32(moof, tfhd.PayloadStart) & 0xFFFFFF;
            var trackId = U32(moof, tfhd.PayloadStart + 4);
            if (!tracks.TryGetValue(trackId, out var track)) throw new HlsSegmentException($"a fragment of unknown track {trackId}");
            if ((tfhdFlags & 0x1) != 0) throw new HlsSegmentException("a fragment with an absolute base data offset");
            var p = tfhd.PayloadStart + 8;
            if ((tfhdFlags & 0x2) != 0) p += 4;
            var defaultDuration = track.DefaultDuration;
            var defaultSize = 0u;
            var defaultFlags = track.DefaultFlags;
            if ((tfhdFlags & 0x8) != 0) { defaultDuration = U32(moof, p); p += 4; }
            if ((tfhdFlags & 0x10) != 0) { defaultSize = U32(moof, p); p += 4; }
            if ((tfhdFlags & 0x20) != 0) { defaultFlags = U32(moof, p); p += 4; }
            var baseIsMoof = (tfhdFlags & 0x20000) != 0;

            var tfdt = Need(parts, "tfdt", "a traf");
            var tfdtVersion = moof[tfdt.PayloadStart];
            var decodeTime = tfdtVersion == 1
                ? BinaryPrimitives.ReadUInt64BigEndian(moof.AsSpan(tfdt.PayloadStart + 4))
                : U32(moof, tfdt.PayloadStart + 4);

            ulong durationTicks = 0;
            uint? firstFlags = null;
            long firstOffset = -1, firstSize = 0;
            long dataPos = baseIsMoof ? 0 : trafEnd;
            var samples = 0;
            foreach (var trun in parts)
            {
                if (trun.Type != "trun") continue;
                var vf = U32(moof, trun.PayloadStart);
                var version = vf >> 24;
                var flags = vf & 0xFFFFFF;
                var count = U32(moof, trun.PayloadStart + 4);
                var q = trun.PayloadStart + 8;
                if ((flags & 0x1) != 0)
                {
                    dataPos = (baseIsMoof ? 0 : trafEnd) + BinaryPrimitives.ReadInt32BigEndian(moof.AsSpan(q));
                    q += 4;
                }
                uint? runFirstFlags = null;
                if ((flags & 0x4) != 0) { runFirstFlags = U32(moof, q); q += 4; }
                var perSample = ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0)
                    + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
                if (q + (long)perSample * count > trun.End) throw new HlsSegmentException("a truncated trun box");
                for (var s = 0u; s < count; s++)
                {
                    var duration = defaultDuration;
                    var size = defaultSize;
                    var sampleFlags = s == 0 && runFirstFlags is { } rf ? rf : defaultFlags;
                    if ((flags & 0x100) != 0) { duration = U32(moof, q); q += 4; }
                    if ((flags & 0x200) != 0) { size = U32(moof, q); q += 4; }
                    if ((flags & 0x400) != 0) { sampleFlags = U32(moof, q); q += 4; }
                    if ((flags & 0x800) != 0) q += 4;
                    if (samples == 0)
                    {
                        firstFlags = sampleFlags;
                        firstOffset = dataPos;
                        firstSize = size;
                    }
                    durationTicks += duration;
                    dataPos += size;
                    samples++;
                }
                _ = version;
            }
            trafEnd = dataPos;

            var shift = track.Handler == "vide" ? videoShiftSeconds : audioShiftSeconds;
            var shiftTicks = (ulong)Math.Round(shift * track.Timescale, MidpointRounding.AwayFromZero);
            var shifted = decodeTime + shiftTicks;
            if (tfdtVersion == 1) BinaryPrimitives.WriteUInt64BigEndian(moof.AsSpan(tfdt.PayloadStart + 4), shifted);
            else if (shifted > uint.MaxValue) throw new HlsSegmentException("a 32-bit tfdt would overflow");
            else BinaryPrimitives.WriteUInt32BigEndian(moof.AsSpan(tfdt.PayloadStart + 4), (uint)shifted);

            if (track.Handler != "vide") continue;
            if (seenVideo) throw new HlsSegmentException("a fragment with two video track runs");
            seenVideo = true;
            if (samples == 0) throw new HlsSegmentException("a fragment without video");
            if (((firstFlags ?? 0) >> 16 & 1) != 0) throw new HlsSegmentException("a fragment that starts on a non-sync frame");
            if (!StartsWithIdr(moof, mdat, mdatHeader, firstOffset, firstSize, track.NalLengthSize))
                throw new HlsSegmentException("a fragment whose first frame is not an IDR frame");
            var ms = durationTicks * 1000.0 / track.Timescale;
            if (durationTicks == 0) throw new HlsSegmentException("a fragment of no length");
            if (ms > MaxSegmentMs + 0.5) throw new HlsSegmentException($"a fragment of {ms / 1000:F3} s");
            var start = (double)shifted / track.Timescale;
            video = new Fragment(start, start + (double)durationTicks / track.Timescale,
                Math.Max(1, (int)Math.Round(ms, MidpointRounding.AwayFromZero)));
        }
        if (video is not { } result) throw new HlsSegmentException("a fragment without video");
        BinaryPrimitives.WriteUInt32BigEndian(moof.AsSpan(mfhd.PayloadStart + 4), (uint)sequenceNumber);
        return result;
    }

    /// <summary>True when the sample at moof-relative <paramref name="offset"/> (inside the mdat) holds an IDR NAL unit.</summary>
    private static bool StartsWithIdr(byte[] moof, byte[] mdat, int mdatHeader, long offset, long size, int lengthSize)
    {
        // Moof-relative offsets point past the moof into the mdat that follows it.
        var at = offset - moof.Length;
        if (at < mdatHeader || size <= 0 || at + size > mdat.Length) return false;
        var p = at;
        var end = at + size;
        for (var units = 0; units < 32 && p + lengthSize < end; units++)
        {
            long length = 0;
            for (var i = 0; i < lengthSize; i++) length = (length << 8) | mdat[p + i];
            if (length <= 0 || p + lengthSize + length > end) return false;
            if ((mdat[p + lengthSize] & 0x1F) == 5) return true;
            p += lengthSize + length;
        }
        return false;
    }
}

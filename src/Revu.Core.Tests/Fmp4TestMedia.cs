using System.Buffers.Binary;
using System.Text;

namespace Revu.Core.Tests;

/// <summary>
/// Synthetic fragmented MP4 for the splitter tests: an init shaped like ffmpeg's (ftyp, moov with mvhd, a video and
/// optionally an audio trak, mvex/trex) and fragments shaped like <c>+frag_keyframe+default_base_moof</c> output
/// (moof with mfhd and one traf per track, then an mdat with the samples).
/// </summary>
internal static class Fmp4TestMedia
{
    public const uint VideoTimescale = 15360;   // ffmpeg's for 60 fps
    public const uint AudioTimescale = 48000;
    public const uint Frame = 256;               // one 60 fps frame at 15360
    public const uint AacFrame = 1024;

    public static byte[] U8(byte value) => [value];
    public static byte[] U16(ushort value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); return b; }
    public static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    public static byte[] U64(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); return b; }
    public static byte[] Zeros(int count) => new byte[count];
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    public static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var output = new byte[total];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(output, at);
            at += part.Length;
        }
        return output;
    }

    public static byte[] Box(string type, params byte[][] payload)
    {
        var body = Concat(payload);
        return Concat(U32((uint)(8 + body.Length)), Ascii(type), body);
    }

    /// <summary>The same box with a 64-bit size field.</summary>
    public static byte[] LargeBox(string type, params byte[][] payload)
    {
        var body = Concat(payload);
        return Concat(U32(1), Ascii(type), U64((ulong)(16 + body.Length)), body);
    }

    public static byte[] FullBox(string type, byte version, uint flags, params byte[][] payload) =>
        Box(type, Concat(U8(version), U32(flags)[1..]), Concat(payload));

    private static byte[] Trak(uint trackId, string handler, uint timescale, byte[] sampleEntry) =>
        Box("trak",
            FullBox("tkhd", 0, 3, U32(0), U32(0), U32(trackId), U32(0), U32(0), Zeros(60)),
            Box("mdia",
                FullBox("mdhd", 0, 0, U32(0), U32(0), U32(timescale), U32(0), U16(0x55C4), U16(0)),
                FullBox("hdlr", 0, 0, U32(0), Ascii(handler), Zeros(12), Ascii("Handler\0")),
                Box("minf", Box("stbl", FullBox("stsd", 0, 0, U32(1), sampleEntry)))));

    private static byte[] Avc1(int nalLengthSize) =>
        Box("avc1", Zeros(6), U16(1), Zeros(16), U16(1920), U16(1080), U32(0x00480000), U32(0x00480000), U32(0), U16(1),
            Zeros(32), U16(0x18), U16(0xFFFF),
            Box("avcC", U8(1), U8(0x64), U8(0), U8(0x2A), U8((byte)(0xFC | (nalLengthSize - 1))), U8(0xE0)));

    private static byte[] Mp4a() =>
        Box("mp4a", Zeros(6), U16(1), Zeros(8), U16(2), U16(16), Zeros(4), U32(48000u << 16));

    private static byte[] Trex(uint trackId, uint duration) => FullBox("trex", 0, 0, U32(trackId), U32(1), U32(duration), U32(0), U32(0));

    /// <summary>ftyp + moov: video track 1 at 15360 and, unless <paramref name="audio"/> is false, audio track 2 at 48000.</summary>
    public static byte[] Init(bool audio = true, bool mvex = true, int nalLengthSize = 4, string? extraHandler = null)
    {
        var ftyp = Box("ftyp", Ascii("isom"), U32(512), Ascii("isomiso6iso2avc1mp41"));
        var parts = new List<byte[]>
        {
            FullBox("mvhd", 0, 0, Zeros(96)),
            Trak(1, "vide", VideoTimescale, Avc1(nalLengthSize)),
        };
        if (audio) parts.Add(Trak(2, "soun", AudioTimescale, Mp4a()));
        if (extraHandler is not null) parts.Add(Trak(3, extraHandler, 1000, Box("text")));
        if (mvex)
        {
            var trex = new List<byte[]> { Trex(1, Frame) };
            if (audio) trex.Add(Trex(2, AacFrame));
            parts.Add(Box("mvex", trex.ToArray()));
        }
        return Concat(ftyp, Box("moov", parts.ToArray()));
    }

    /// <summary>One AVCC sample: NAL units of the given types (each one length-prefixed, a few bytes long).</summary>
    public static byte[] Sample(int nalLengthSize, params int[] nalTypes)
    {
        var units = new List<byte[]>();
        foreach (var type in nalTypes)
        {
            var body = Concat(U8((byte)(0x60 | type)), Ascii("payload"));
            var length = U32((uint)body.Length)[(4 - nalLengthSize)..];
            units.Add(Concat(length, body));
        }
        return Concat(units.ToArray());
    }

    /// <summary>What one fragment holds.</summary>
    public sealed record FragmentSpec
    {
        public uint Sequence { get; init; } = 1;
        public ulong VideoTfdt { get; init; }
        public int VideoFrames { get; init; } = 120;
        public uint VideoFrameDuration { get; init; } = Frame;
        /// <summary>The first frame's NAL types: AUD then IDR by default.</summary>
        public int[] FirstNals { get; init; } = [9, 5];
        public bool FirstSync { get; init; } = true;
        public ulong? AudioTfdt { get; init; }
        public int AudioFrames { get; init; } = 94;
        public byte TfdtVersion { get; init; } = 1;
        public int NalLengthSize { get; init; } = 4;
        public bool LargeMdat { get; init; }
    }

    /// <summary>A moof and its mdat (default-base-is-moof data offsets, per-sample durations and sizes).</summary>
    public static (byte[] Moof, byte[] Mdat) Fragment(FragmentSpec spec)
    {
        var videoSamples = new List<byte[]> { Sample(spec.NalLengthSize, spec.FirstNals) };
        for (var i = 1; i < spec.VideoFrames; i++) videoSamples.Add(Sample(spec.NalLengthSize, 1));
        var audioSamples = new List<byte[]>();
        if (spec.AudioTfdt is not null)
            for (var i = 0; i < spec.AudioFrames; i++) audioSamples.Add(Ascii("aac" + i));

        byte[] Build(int moofLength)
        {
            var mdatHeader = spec.LargeMdat ? 16 : 8;
            var videoOffset = moofLength + mdatHeader;
            var trafs = new List<byte[]>
            {
                Traf(1, spec.VideoTfdt, spec.TfdtVersion, videoOffset, videoSamples, spec.VideoFrameDuration,
                    spec.FirstSync ? 0x02000000u : 0x01010000u),
            };
            if (spec.AudioTfdt is { } audioTfdt)
            {
                var audioOffset = videoOffset + videoSamples.Sum(s => s.Length);
                trafs.Add(Traf(2, audioTfdt, spec.TfdtVersion, audioOffset, audioSamples, AacFrame, null));
            }
            return Box("moof", [FullBox("mfhd", 0, 0, U32(spec.Sequence)), .. trafs]);
        }

        var moof = Build(0);
        moof = Build(moof.Length);
        var data = Concat([.. videoSamples, .. audioSamples]);
        var mdat = spec.LargeMdat ? LargeBox("mdat", data) : Box("mdat", data);
        return (moof, mdat);
    }

    private static byte[] Traf(uint trackId, ulong tfdt, byte tfdtVersion, int dataOffset, List<byte[]> samples,
        uint duration, uint? firstFlags)
    {
        var tfhd = FullBox("tfhd", 0, 0x020000, U32(trackId));
        var tfdtBox = tfdtVersion == 1 ? FullBox("tfdt", 1, 0, U64(tfdt)) : FullBox("tfdt", 0, 0, U32((uint)tfdt));
        var flags = 0x1u | 0x100u | 0x200u | (firstFlags is null ? 0u : 0x4u);
        var fields = new List<byte[]> { U32((uint)samples.Count), U32((uint)dataOffset) };
        if (firstFlags is { } first) fields.Add(U32(first));
        foreach (var sample in samples)
        {
            fields.Add(U32(duration));
            fields.Add(U32((uint)sample.Length));
        }
        return Box("traf", tfhd, tfdtBox, FullBox("trun", 0, flags, fields.ToArray()));
    }

    /// <summary>Fragments of 2 s (120 frames) from video time <paramref name="startTicks"/>, with audio when asked.</summary>
    public static byte[] Stream(int fragments, bool audio = true, ulong startTicks = 0, int lastFrames = 120,
        bool mfra = false, byte[]? init = null)
    {
        var parts = new List<byte[]> { init ?? Init(audio) };
        for (var i = 0; i < fragments; i++)
        {
            var frames = i == fragments - 1 ? lastFrames : 120;
            var video = startTicks + (ulong)i * 120 * Frame;
            var (moof, mdat) = Fragment(new FragmentSpec
            {
                Sequence = (uint)(i + 1),
                VideoTfdt = video,
                VideoFrames = frames,
                AudioTfdt = audio ? (ulong)i * 2 * AudioTimescale : null,
            });
            parts.Add(moof);
            parts.Add(mdat);
        }
        if (mfra) parts.Add(Box("mfra", FullBox("mfro", 0, 0, U32(16))));
        return Concat(parts.ToArray());
    }

    /// <summary>A stream that hands out at most <paramref name="chunk"/> bytes per read, or splits once at <paramref name="splitAt"/>.</summary>
    public sealed class ChunkedStream(byte[] data, int chunk = int.MaxValue, int splitAt = -1) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var limit = Math.Min(count, chunk);
            if (splitAt > _position) limit = Math.Min(limit, splitAt - _position);
            var n = Math.Min(limit, data.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The (mfhd sequence, per-track tfdt) of a segment file the splitter wrote.</summary>
    public static (uint Sequence, Dictionary<uint, ulong> Tfdt) ReadSegment(byte[] segment)
    {
        var moof = Revu.Core.Services.Fmp4Splitter.Children(segment, 0, segment.Length).First(b => b.Type == "moof");
        var inner = Revu.Core.Services.Fmp4Splitter.Children(segment, moof.PayloadStart, moof.End);
        var mfhd = inner.First(b => b.Type == "mfhd");
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan(mfhd.PayloadStart + 4));
        var tfdts = new Dictionary<uint, ulong>();
        foreach (var traf in inner.Where(b => b.Type == "traf"))
        {
            var parts = Revu.Core.Services.Fmp4Splitter.Children(segment, traf.PayloadStart, traf.End);
            var tfhd = parts.First(b => b.Type == "tfhd");
            var trackId = BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan(tfhd.PayloadStart + 4));
            var tfdt = parts.First(b => b.Type == "tfdt");
            tfdts[trackId] = segment[tfdt.PayloadStart] == 1
                ? BinaryPrimitives.ReadUInt64BigEndian(segment.AsSpan(tfdt.PayloadStart + 4))
                : BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan(tfdt.PayloadStart + 4));
        }
        return (sequence, tfdts);
    }
}

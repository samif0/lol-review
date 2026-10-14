using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Encoder selection, the one retry, progress, verification and output cleanup, with ffmpeg
/// faked (no process is ever started here).
/// </summary>
public sealed class VodShareEncoderSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "vodenc-" + Guid.NewGuid().ToString("N"));

    public VodShareEncoderSelectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A scripted ffmpeg: which encoders each binary lists, which probes pass, how encodes go.</summary>
    private sealed class FakeFfmpeg
    {
        public Dictionary<string, string[]> Listed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string Bin, string Encoder)> ProbeFails { get; } = new();
        /// <summary>Pairs whose probe fails once any encode has run (an encoder that broke at runtime).</summary>
        public HashSet<(string Bin, string Encoder)> ProbeFailsAfterEncode { get; } = new();
        public Queue<int> EncodeExits { get; } = new();
        public Queue<bool> EncodeTimeouts { get; } = new();
        public double SourceStart { get; set; }
        private int _encodes;
        public double OutputDuration { get; set; } = 60;
        public double SourceDuration { get; set; } = 60;
        public bool WriteFtyp { get; set; } = true;
        public Func<CancellationToken, Task>? DuringEncode { get; set; }
        public List<(string Bin, string Kind, IReadOnlyList<string> Args)> Calls { get; } = new();

        public async Task<VodEncoderRun> Run(string bin, IReadOnlyList<string> args, int timeout, Action<string>? onLine,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Contains("-encoders"))
            {
                Calls.Add((bin, "list", args));
                foreach (var name in Listed.GetValueOrDefault(bin, []))
                    onLine?.Invoke($" V....D {name,-20} some encoder");
                return new VodEncoderRun(0, "", false);
            }
            if (args.Contains("lavfi"))
            {
                var encoder = args[args.ToList().IndexOf("-c:v") + 1];
                Calls.Add((bin, "probe:" + encoder, args));
                var fails = ProbeFails.Contains((bin, encoder)) || (_encodes > 0 && ProbeFailsAfterEncode.Contains((bin, encoder)));
                return new VodEncoderRun(fails ? 1 : 0, "", false);
            }
            if (args.Contains("-progress"))
            {
                var encoder = args[args.ToList().IndexOf("-c:v") + 1];
                Calls.Add((bin, "encode:" + encoder, args));
                _encodes++;
                var output = args[^1];
                await File.WriteAllBytesAsync(output, WriteFtyp
                    ? [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 1, 2, 3, 4]
                    : [1, 2, 3, 4, 5, 6, 7, 8, 9], ct);
                onLine?.Invoke("out_time_us=N/A");
                onLine?.Invoke("out_time_us=15000000");
                onLine?.Invoke("out_time_us=15500000");
                onLine?.Invoke("out_time_us=30000000");
                if (DuringEncode is not null) await DuringEncode(ct);
                onLine?.Invoke("progress=end");
                if (EncodeTimeouts.Count > 0 && EncodeTimeouts.Dequeue()) return new VodEncoderRun(-1, "", true);
                var exit = EncodeExits.Count > 0 ? EncodeExits.Dequeue() : 0;
                return new VodEncoderRun(exit, exit == 0 ? "" : "Error while encoding", false);
            }
            // Duration probe: "-hide_banner -i <file>" prints the input summary and exits 1.
            var file = args[^1];
            Calls.Add((bin, "duration", args));
            var isSource = file.EndsWith("source.mp4", StringComparison.Ordinal);
            var seconds = isSource ? SourceDuration : OutputDuration;
            var ts = TimeSpan.FromSeconds(seconds);
            var start = (isSource ? SourceStart : 0).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
            return new VodEncoderRun(1, $"  Duration: {(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}, start: {start}", false);
        }
    }

    private VodShareEncoder Create(FakeFfmpeg fake, params string[] binaries) =>
        new(null, NullLogger.Instance, _ => Task.FromResult<IReadOnlyList<string>>(binaries), fake.Run);

    private string Source()
    {
        var path = Path.Combine(_dir, "source.mp4");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private sealed class Collect : IProgress<int>
    {
        public List<int> Items { get; } = new();
        public void Report(int value) { lock (Items) Items.Add(value); }
    }

    [Fact]
    public async Task HardwareOnAnyBinary_BeatsTheCpuEncoder()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["libx264", "h264_mf"];
        fake.Listed["B"] = ["libx264", "h264_amf", "h264_nvenc"];
        var encoder = Create(fake, "A", "B");

        var choice = await encoder.SelectAsync(CancellationToken.None);

        Assert.Equal(new VodEncoderChoice("B", "h264_nvenc"), choice);
        // Each binary was listed once; only the winning pair was probed.
        Assert.Equal(2, fake.Calls.Count(c => c.Kind == "list"));
        Assert.Equal(new[] { "probe:h264_nvenc" }, fake.Calls.Where(c => c.Kind.StartsWith("probe")).Select(c => c.Kind));
    }

    [Fact]
    public async Task AFailedProbe_FallsThroughThePriorityList_AndTheWinnerIsCached()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264"];
        fake.ProbeFails.Add(("A", "h264_nvenc"));
        fake.ProbeFails.Add(("A", "h264_amf"));
        fake.ProbeFails.Add(("A", "h264_qsv"));
        var encoder = Create(fake, "A");

        Assert.Equal(new VodEncoderChoice("A", "h264_mf"), await encoder.SelectAsync(CancellationToken.None));
        var calls = fake.Calls.Count;
        Assert.Equal(new VodEncoderChoice("A", "h264_mf"), await encoder.SelectAsync(CancellationToken.None));
        Assert.Equal(calls, fake.Calls.Count);
    }

    [Fact]
    public async Task OnlyLibx264_IsChosenLast_AndNothingUsable_IsNull()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["libx264"];
        Assert.Equal(new VodEncoderChoice("A", "libx264"), await Create(fake, "A").SelectAsync(CancellationToken.None));

        var none = new FakeFfmpeg();
        none.Listed["A"] = ["libx264"];
        none.ProbeFails.Add(("A", "libx264"));
        Assert.Null(await Create(none, "A").SelectAsync(CancellationToken.None));
        Assert.Null(await Create(new FakeFfmpeg()).SelectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Encode_ReportsDistinctPercents_ThenOneHundred_AndVerifies()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc"];
        var encoder = Create(fake, "A");
        var output = Path.Combine(_dir, "out", "game-1.mp4");
        var progress = new Collect();

        var result = await encoder.EncodeAsync(Source(), output, VodShareQuality.P1080, 30, 60, progress, CancellationToken.None);

        Assert.Equal((output, "h264_nvenc", VodShareQuality.P1080, 30, 4500),
            (result.OutputPath, result.Encoder, result.Quality, result.Fps, result.VideoKbps));
        Assert.Equal(12, result.SizeBytes);
        Assert.Equal(new[] { 25, 50, 100 }, progress.Items);
        var encode = fake.Calls.Single(c => c.Kind.StartsWith("encode"));
        Assert.Equal(output, encode.Args[^1]);
    }

    [Fact]
    public async Task A60FpsEncode_KeepsSixtyFrames_AndItsBitrate()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc"];

        var result = await Create(fake, "A").EncodeAsync(Source(), Path.Combine(_dir, "sixty.mp4"), VodShareQuality.P1080, 60,
            60, null, CancellationToken.None);

        Assert.Equal((60, 5000), (result.Fps, result.VideoKbps));
        var args = string.Join(" ", fake.Calls.Single(c => c.Kind.StartsWith("encode")).Args);
        Assert.Contains("-vf fps=60,", args);
        Assert.Contains("-b:v 5000k -maxrate 7500k -bufsize 10000k", args);
        Assert.Contains("-g 120", args);
    }

    [Fact]
    public async Task BeforeRetry_RunsOnce_AfterTheFailedOutputIsGone_AndBeforeTheSecondEncoderWrites()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc", "libx264"];
        fake.EncodeExits.Enqueue(1);
        var output = Path.Combine(_dir, "retry-hook.mp4");
        var seen = new List<(int Encodes, bool OutputExists)>();

        var result = await Create(fake, "A").EncodeAsync(Source(), output, VodShareQuality.P1080, 60, 60, null,
            CancellationToken.None, beforeRetry: _ =>
            {
                seen.Add((fake.Calls.Count(c => c.Kind.StartsWith("encode")), File.Exists(output)));
                return Task.CompletedTask;
            });

        Assert.Equal("libx264", result.Encoder);
        Assert.Equal(new[] { (1, false) }, seen);

        // A first run that succeeds never calls it.
        var clean = new FakeFfmpeg();
        clean.Listed["A"] = ["h264_nvenc"];
        var calls = 0;
        await Create(clean, "A").EncodeAsync(Source(), Path.Combine(_dir, "clean.mp4"), VodShareQuality.P1080, 60, 60, null,
            CancellationToken.None, beforeRetry: _ => { calls++; return Task.CompletedTask; });
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Padding_AddsATrailingFreeBox_OfExactlyTheGap()
    {
        var path = Path.Combine(_dir, "pad.mp4");
        File.WriteAllBytes(path, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 1, 2, 3, 4]);

        Assert.True(VodShareEncoder.TryPadTo(path, 12 + 70_000));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(12 + 70_000, bytes.Length);
        Assert.Equal(70_000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12, 4)));
        Assert.Equal("free", System.Text.Encoding.ASCII.GetString(bytes, 16, 4));
        Assert.All(bytes.Skip(20), b => Assert.Equal(0, b));
        // Already the size: nothing to add. Larger, or short by fewer bytes than a box header: no.
        Assert.True(VodShareEncoder.TryPadTo(path, bytes.Length));
        Assert.False(VodShareEncoder.TryPadTo(path, bytes.Length - 1));
        Assert.False(VodShareEncoder.TryPadTo(path, bytes.Length + 7));
        Assert.Equal(12 + 70_000, new FileInfo(path).Length);
    }

    [Fact]
    public async Task AFailedEncode_WhoseEncoderNowFailsItsProbe_IsMarkedBroken_AndRetriedOnTheNext()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc", "libx264"];
        fake.ProbeFailsAfterEncode.Add(("A", "h264_nvenc"));
        fake.EncodeExits.Enqueue(1);
        var encoder = Create(fake, "A");
        var output = Path.Combine(_dir, "retry.mp4");

        var result = await encoder.EncodeAsync(Source(), output, VodShareQuality.P720, 30, 60, null, CancellationToken.None);

        Assert.Equal("libx264", result.Encoder);
        Assert.Equal(new[] { "encode:h264_nvenc", "encode:libx264" },
            fake.Calls.Where(c => c.Kind.StartsWith("encode")).Select(c => c.Kind));
        Assert.Contains("-threads", fake.Calls.Last(c => c.Kind == "encode:libx264").Args);
        // The broken pair stays out of later selections.
        Assert.Equal(new VodEncoderChoice("A", "libx264"), await encoder.SelectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFailedEncode_WhoseEncoderStillProbes_RetriesOnce_ButKeepsTheEncoderForLaterShares()
    {
        // A truncated import or a full disk fails the run; the hardware encoder itself is fine.
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc", "libx264"];
        fake.EncodeExits.Enqueue(1);
        var encoder = Create(fake, "A");

        var result = await encoder.EncodeAsync(Source(), Path.Combine(_dir, "bad-source.mp4"), VodShareQuality.P1080, 30, 60, null,
            CancellationToken.None);

        Assert.Equal("libx264", result.Encoder);
        Assert.Equal(new VodEncoderChoice("A", "h264_nvenc"), await encoder.SelectAsync(CancellationToken.None));
        var next = await encoder.EncodeAsync(Source(), Path.Combine(_dir, "next.mp4"), VodShareQuality.P1080, 30, 60, null,
            CancellationToken.None);
        Assert.Equal("h264_nvenc", next.Encoder);
    }

    [Fact]
    public async Task ATimedOutEncode_WithAWorkingEncoder_FailsWithoutASecondRun_AndKeepsTheEncoder()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc", "libx264"];
        fake.EncodeTimeouts.Enqueue(true);
        var encoder = Create(fake, "A");
        var output = Path.Combine(_dir, "slow.mp4");

        var ex = await Assert.ThrowsAsync<VodEncodeException>(() =>
            encoder.EncodeAsync(Source(), output, VodShareQuality.P1080, 30, 60, null, CancellationToken.None));

        Assert.Equal("encode_failed", ex.Code);
        Assert.Single(fake.Calls, c => c.Kind.StartsWith("encode"));
        Assert.False(File.Exists(output));
        Assert.Equal(new VodEncoderChoice("A", "h264_nvenc"), await encoder.SelectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASourceThatStartsLate_IsEncodedWithItsOffset_AndOneAtZeroWithout()
    {
        var fake = new FakeFfmpeg { SourceStart = 4.956 };
        fake.Listed["A"] = ["h264_nvenc"];
        var encoder = Create(fake, "A");
        await encoder.EncodeAsync(Source(), Path.Combine(_dir, "late.mp4"), VodShareQuality.P1080, 30, 60, null, CancellationToken.None);
        var args = fake.Calls.Single(c => c.Kind.StartsWith("encode")).Args.ToList();
        Assert.Equal("4.956", args[args.IndexOf("-output_ts_offset") + 1]);

        var zero = new FakeFfmpeg();
        zero.Listed["A"] = ["h264_nvenc"];
        await Create(zero, "A").EncodeAsync(Source(), Path.Combine(_dir, "zero.mp4"), VodShareQuality.P1080, 30, 60, null,
            CancellationToken.None);
        Assert.DoesNotContain("-output_ts_offset", zero.Calls.Single(c => c.Kind.StartsWith("encode")).Args);
    }

    [Fact]
    public async Task ASecondFailure_IsTerminal_AndDeletesTheOutput()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc", "libx264"];
        fake.EncodeExits.Enqueue(1);
        fake.EncodeExits.Enqueue(1);
        var output = Path.Combine(_dir, "fail.mp4");

        var ex = await Assert.ThrowsAsync<VodEncodeException>(() =>
            Create(fake, "A").EncodeAsync(Source(), output, VodShareQuality.P1080, 30, 60, null, CancellationToken.None));

        Assert.Equal("encode_failed", ex.Code);
        Assert.Equal(2, fake.Calls.Count(c => c.Kind.StartsWith("encode")));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task NoEncoder_IsEncodeFailed()
    {
        var ex = await Assert.ThrowsAsync<VodEncodeException>(() =>
            Create(new FakeFfmpeg()).EncodeAsync(Source(), Path.Combine(_dir, "x.mp4"), VodShareQuality.P1080, 30, 60, null,
                CancellationToken.None));
        Assert.Equal("encode_failed", ex.Code);
    }

    [Fact]
    public async Task Cancel_DeletesTheOutput_AndThrows()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc"];
        using var cts = new CancellationTokenSource();
        fake.DuringEncode = ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var output = Path.Combine(_dir, "cancel.mp4");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(fake, "A").EncodeAsync(Source(), output, VodShareQuality.P1080, 30, 60, null, cts.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task NotAnMp4_OrTheWrongLength_IsEncodeFailed_AndDeleted()
    {
        var fake = new FakeFfmpeg { WriteFtyp = false };
        fake.Listed["A"] = ["h264_nvenc"];
        var output = Path.Combine(_dir, "noftyp.mp4");
        var ex = await Assert.ThrowsAsync<VodEncodeException>(() =>
            Create(fake, "A").EncodeAsync(Source(), output, VodShareQuality.P1080, 30, 60, null, CancellationToken.None));
        Assert.Equal("encode_failed", ex.Code);
        Assert.False(File.Exists(output));

        var shortFake = new FakeFfmpeg { OutputDuration = 50, SourceDuration = 60 };
        shortFake.Listed["A"] = ["h264_nvenc"];
        var shortOut = Path.Combine(_dir, "short.mp4");
        await Assert.ThrowsAsync<VodEncodeException>(() =>
            Create(shortFake, "A").EncodeAsync(Source(), shortOut, VodShareQuality.P1080, 30, 60, null, CancellationToken.None));
        Assert.False(File.Exists(shortOut));

        // Within max(2 s, 1%) passes.
        var close = new FakeFfmpeg { OutputDuration = 61, SourceDuration = 60 };
        close.Listed["A"] = ["h264_nvenc"];
        var ok = await Create(close, "A").EncodeAsync(Source(), Path.Combine(_dir, "close.mp4"), VodShareQuality.P1080, 30, 60,
            null, CancellationToken.None);
        Assert.True(File.Exists(ok.OutputPath));
    }

    [Fact]
    public async Task A720pGamePastTwoHours_IsTooLong_WithoutRunningFfmpeg()
    {
        var fake = new FakeFfmpeg();
        fake.Listed["A"] = ["h264_nvenc"];
        var ex = await Assert.ThrowsAsync<VodEncodeException>(() =>
            Create(fake, "A").EncodeAsync(Source(), Path.Combine(_dir, "long.mp4"), VodShareQuality.P720, 30, 7300, null,
                CancellationToken.None));
        Assert.Equal("too_long", ex.Code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Candidates_OverrideOnlyWithADataRoot_DeduplicatedAndExistingOnly()
    {
        var a = Path.Combine(_dir, "a", "ffmpeg.exe");
        var b = Path.Combine(_dir, "b", "revu-media-ffmpeg.exe");
        var dev = Path.Combine(_dir, "dev", "ffmpeg.exe");
        var existing = new HashSet<string>(new[] { a, b, dev }, StringComparer.OrdinalIgnoreCase);
        bool Exists(string p) => existing.Contains(p);

        Assert.Equal(new[] { dev, a, b }, VodShareEncoder.Candidates(dev, _dir, a, b, Exists));
        Assert.Equal(new[] { a, b }, VodShareEncoder.Candidates(dev, null, a, b, Exists));
        Assert.Equal(new[] { a, b }, VodShareEncoder.Candidates("ffmpeg.exe", _dir, a, b, Exists));
        Assert.Equal(new[] { a }, VodShareEncoder.Candidates(null, null, a, a.ToUpperInvariant(), Exists));
        Assert.Equal(new[] { b }, VodShareEncoder.Candidates(null, null, Path.Combine(_dir, "missing.exe"), b, Exists));
        Assert.Empty(VodShareEncoder.Candidates(null, null, null, Path.Combine(_dir, "none.exe"), Exists));
    }
}

/// <summary>Opt-in only (REVU_VOD_ENCODE_TEST=1): runs real ffmpeg. Never on in CI.</summary>
public sealed class VodEncodeFactAttribute : FactAttribute
{
    public VodEncodeFactAttribute(bool needsSource = false)
    {
        if (Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_TEST") != "1")
            Skip = "Real share encodes are opt-in: set REVU_VOD_ENCODE_TEST=1.";
        else if (needsSource && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_SOURCE")))
            Skip = "Set REVU_VOD_ENCODE_SOURCE to a read-only recording to encode a 60 s cut of it.";
    }
}

/// <summary>
/// Real encodes with the encoder <see cref="VodShareEncoder.SelectAsync"/> picks. Outputs go to
/// REVU_VOD_ENCODE_OUT (else a temp folder); a source recording is only ever read.
/// </summary>
public sealed class VodShareEncoderRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string OutDir()
    {
        var dir = Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_OUT");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "vodenc-real");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static VodShareEncoder Encoder() =>
        new(new NarrationMixer(NullLogger<NarrationMixer>.Instance), NullLogger<VodShareEncoder>.Instance);

    private static async Task<double?> StartTimeAsync(string ffmpeg, string file)
    {
        var run = await FfmpegRunner.RunAsync(ffmpeg, ["-hide_banner", "-i", file], 60, CancellationToken.None);
        var m = System.Text.RegularExpressions.Regex.Match(run.StderrTail, @"start: (-?\d+(?:\.\d+)?)");
        return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [VodEncodeFact]
    public async Task SyntheticInput_EncodesAndVerifies()
    {
        var encoder = Encoder();
        var choice = await encoder.SelectAsync(CancellationToken.None);
        Assert.NotNull(choice);
        var dir = OutDir();
        var source = Path.Combine(dir, "synthetic-source.mp4");
        var made = await FfmpegRunner.RunAsync(choice!.FfmpegPath,
            ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=12",
             "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:d=12", "-c:v", "mpeg4", "-q:v", "4",
             "-c:a", "aac", "-shortest", source], 120, CancellationToken.None);
        Assert.True(made.Succeeded, made.StderrTail);

        var outPath = Path.Combine(dir, "synthetic-share.mp4");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await encoder.EncodeAsync(source, outPath, VodShareQuality.P1080, 30, 12, null, CancellationToken.None);
        output.WriteLine($"synthetic: {choice.Encoder} via {choice.FfmpegPath}: {result.SizeBytes} bytes in {watch.Elapsed.TotalSeconds:F1} s");
        Assert.True(VodShareEncoder.HasFtyp(outPath));
    }

    [VodEncodeFact]
    public async Task SyntheticLateStart_KeepsTheSourceStartTime()
    {
        var encoder = Encoder();
        var choice = await encoder.SelectAsync(CancellationToken.None);
        Assert.NotNull(choice);
        var dir = OutDir();
        var source = Path.Combine(dir, "late-source.mp4");
        var made = await FfmpegRunner.RunAsync(choice!.FfmpegPath,
            ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=10",
             "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:d=10", "-c:v", "mpeg4", "-q:v", "4",
             "-c:a", "aac", "-shortest", "-output_ts_offset", "4.978", source], 120, CancellationToken.None);
        Assert.True(made.Succeeded, made.StderrTail);

        var outPath = Path.Combine(dir, "late-share.mp4");
        await encoder.EncodeAsync(source, outPath, VodShareQuality.P720, 30, 10, null, CancellationToken.None);
        var sourceStart = await StartTimeAsync(choice.FfmpegPath, source);
        var outStart = await StartTimeAsync(choice.FfmpegPath, outPath);
        output.WriteLine($"late start: source {sourceStart} share {outStart} ({choice.Encoder})");
        Assert.True(sourceStart > 4.5);
        Assert.True(Math.Abs(outStart!.Value - sourceStart!.Value) <= 0.05);
    }

    [VodEncodeFact(needsSource: true)]
    public async Task RealRecording_SixtySecondCut_1080pAnd720p()
    {
        var source = Environment.GetEnvironmentVariable("REVU_VOD_ENCODE_SOURCE")!;
        var sourceInfo = new FileInfo(source);
        var before = (sourceInfo.Length, sourceInfo.LastWriteTimeUtc);
        var encoder = Encoder();
        var choice = await encoder.SelectAsync(CancellationToken.None);
        Assert.NotNull(choice);
        var dir = OutDir();

        // A 60 s stream copy from 20:00, read-only on the source.
        var cut = Path.Combine(dir, "cut60.mp4");
        var copied = await FfmpegRunner.RunAsync(choice!.FfmpegPath,
            ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-ss", "1200", "-i", source, "-t", "60",
             "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-avoid_negative_ts", "make_zero", cut], 300, CancellationToken.None);
        Assert.True(copied.Succeeded, copied.StderrTail);

        var cutStart = await StartTimeAsync(choice.FfmpegPath, cut);
        var fps = VodShareEncoderArgs.Fps((await new NarrationMixer(NullLogger<NarrationMixer>.Instance).ProbeAsync(cut, CancellationToken.None))?.FrameRate);
        foreach (var quality in new[] { VodShareQuality.P1080, VodShareQuality.P720 })
        {
            var outPath = Path.Combine(dir, $"cut60-{VodShareEncoderArgs.Label(quality, fps)}.mp4");
            var progress = new List<int>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await encoder.EncodeAsync(cut, outPath, quality, fps, 60, new Progress<int>(p => { lock (progress) progress.Add(p); }),
                CancellationToken.None);
            var seconds = watch.Elapsed.TotalSeconds;
            var outStart = await StartTimeAsync(choice.FfmpegPath, outPath);
            output.WriteLine($"{VodShareEncoderArgs.Label(quality, fps)} {result.Encoder} ({choice.FfmpegPath}): {result.SizeBytes} bytes "
                + $"({result.SizeBytes / 1e6:F1} MB/min), {seconds:F1} s ({60 / seconds:F1}x), start {outStart} vs cut {cutStart}");
            await File.AppendAllTextAsync(Path.Combine(dir, "results.txt"),
                $"{DateTime.Now:O} {VodShareEncoderArgs.Label(quality, fps)} encoder={result.Encoder} binary={choice.FfmpegPath} "
                + $"bytes={result.SizeBytes} mbPerMin={result.SizeBytes / 1e6:F2} seconds={seconds:F2} speed={60 / seconds:F1}x "
                + $"start={outStart} cutStart={cutStart}{Environment.NewLine}");
            Assert.NotNull(outStart);
            Assert.True(Math.Abs(outStart!.Value - (cutStart ?? 0)) <= 0.05);
        }

        sourceInfo.Refresh();
        Assert.Equal(before, (sourceInfo.Length, sourceInfo.LastWriteTimeUtc));
    }
}

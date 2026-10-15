using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar.Tests;

/// <summary>VOD share proxy client double: the test decides status, upload and markers outcomes.</summary>
internal sealed class FakeVodClient : IVodShareClient
{
    public const long Now = 1_760_400_000;

    public static VodShareServerStatus Allowed(int live = 0, int pending = 0, int startsToday = 0, long freeBytes = 7_000_000_000,
        bool sharing = true, IReadOnlyList<VodSharePoolRelease>? releases = null, IReadOnlyList<VodShareServerShare>? shares = null) =>
        new(true, true, sharing, true, Now,
            new VodShareServerLimits(2147483648L, 7200, 259200, 3, 1, 6, 16777216, 49152, 14336, 200),
            new VodShareServerUsage(live, pending, startsToday, Now + 86_400),
            new VodSharePool(9_000_000_000, 7_000_000_000, 7_000_000_000 - freeBytes, 7_000_000_000 - freeBytes, freeBytes,
                releases ?? []),
            shares ?? []);

    public Func<VodShareServerStatus> Status { get; set; } = () => Allowed();
    public Exception? StatusError { get; set; }
    public int StatusCalls { get; private set; }

    /// <summary>What init reports for an upload with slug <paramref name="id"/>.</summary>
    public static VodUploadStarted Started(string id) => new(id, "https://revu.lol/v/" + id, Now + 259_200);

    /// <summary>
    /// The upload after its init for a finished file (UploadVodAsync), or after the copy is
    /// written for one that started with the encode. Only the first reports the start: the
    /// second was reported at init (<see cref="InitStarted"/>), like the real client does.
    /// </summary>
    public Func<string, VodUploadRequest, IProgress<ClipUploadProgress>?, Action<VodUploadStarted>?, CancellationToken, Task<VodUploadResult>> OnUpload { get; set; } =
        (_, _, progress, assign, _) =>
        {
            assign?.Invoke(Started("vod1234"));
            progress?.Report(new ClipUploadProgress("uploading", 0, 100));
            progress?.Report(new ClipUploadProgress("uploading", 61, 100));
            progress?.Report(new ClipUploadProgress("finishing", 100, 100));
            return Task.FromResult(new VodUploadResult("vod1234", "https://revu.lol/v/vod1234", Now + 259_200));
        };

    /// <summary>What init reports for an upload that starts with the encode, before anything is written.</summary>
    public VodUploadStarted InitStarted { get; set; } = Started("vod1234");

    /// <summary>
    /// Uploads that went through: Declared is the reserved size of one that ran while the copy was
    /// encoded (recorded once the copy was final), null for one of a finished file.
    /// </summary>
    public List<(string File, VodUploadRequest Request, bool FileExisted, long? Declared)> Uploads { get; } = new();
    /// <summary>Every upload started with the encode, and how it ended (true written, false given up, null open).</summary>
    public List<(VodUploadRequest Request, long Declared, bool EncodeDone)> WhileWriting { get; } = new();
    public List<bool?> WhileWritingOutcomes { get; } = new();
    /// <summary>A refusal the proxy gives at init, before anything is written.</summary>
    public Exception? InitError { get; set; }
    /// <summary>Set by the encoder double while it encodes (to see the upload start before the encode ends).</summary>
    public Func<bool> EncodeDone { get; set; } = () => false;
    /// <summary>Every markers PUT, and whether its upload had completed (the share was ready) when it was sent.</summary>
    public List<(string Id, string Json, bool Ready)> Markers { get; } = new();
    public Func<int, Exception?> MarkerError { get; set; } = _ => null;
    /// <summary>Runs inside each markers PUT once it is recorded (a test holds one in flight here).</summary>
    public Func<string, CancellationToken, Task>? DuringMarkers { get; set; }
    /// <summary>Like a proxy that takes markers only for a ready share: one still uploading is a 404.</summary>
    public bool MarkersOnlyWhenReady { get; set; }
    /// <summary>Uploads that completed: the proxy has them as ready shares.</summary>
    public HashSet<string> Ready { get; } = new();

    public Task<VodShareServerStatus> GetVodShareStatusAsync(string sessionToken, CancellationToken ct = default)
    {
        StatusCalls++;
        if (StatusError is not null) return Task.FromException<VodShareServerStatus>(StatusError);
        return Task.FromResult(Status());
    }

    public Task<VodUploadResult> UploadVodAsync(string filePath, string sessionToken, VodUploadRequest request,
        IProgress<ClipUploadProgress>? progress = null, Action<VodUploadStarted>? onStarted = null, CancellationToken ct = default)
    {
        lock (Uploads) Uploads.Add((filePath, request, File.Exists(filePath), null));
        return CompletedAsync(OnUpload(filePath, request, progress, onStarted, ct));
    }

    private async Task<VodUploadResult> CompletedAsync(Task<VodUploadResult> upload)
    {
        var done = await upload;
        lock (Markers) Ready.Add(done.Id);
        return done;
    }

    public async Task<VodUploadResult> UploadVodWhileWritingAsync(string filePath, long declaredSize, Task<bool> written,
        string sessionToken, VodUploadRequest request, IProgress<ClipUploadProgress>? progress = null,
        Action<VodUploadStarted>? onStarted = null, CancellationToken ct = default)
    {
        int index;
        lock (WhileWriting)
        {
            WhileWriting.Add((request, declaredSize, EncodeDone()));
            WhileWritingOutcomes.Add(null);
            index = WhileWriting.Count - 1;
        }
        // Like the real client: a refusal comes back from init at once, an accepted init reports
        // its link before anything is written, and nothing completes until the copy is written;
        // a copy that was given up throws once the remote is gone.
        await Task.Yield();
        if (InitError is { } refused) throw refused;
        onStarted?.Invoke(InitStarted);
        bool ok;
        try { ok = await written.WaitAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { ok = false; }
        lock (WhileWriting) WhileWritingOutcomes[index] = ok;
        if (!ok) throw new VodCopyAbandonedException();
        lock (Uploads) Uploads.Add((filePath, request, File.Exists(filePath), declaredSize));
        return await CompletedAsync(OnUpload(filePath, request, progress, null, ct));
    }

    public async Task PutVodMarkersAsync(string id, string sessionToken, string markersJson, CancellationToken ct = default)
    {
        int attempt;
        bool ready;
        lock (Markers)
        {
            ready = Ready.Contains(id);
            Markers.Add((id, markersJson, ready));
            attempt = Markers.Count;
        }
        if (DuringMarkers is { } during) await during(id, ct);
        if (MarkersOnlyWhenReady && !ready) throw new ClipUploadException("Not found") { Code = "not_found" };
        if (MarkerError(attempt) is { } error) throw error;
    }
}

/// <summary>Encoder double: writes a small "mp4" and reports the size the test chooses.</summary>
internal sealed class FakeVodEncoder : IVodShareEncoder
{
    public List<(string Source, string Output, VodShareQuality Quality, int Fps, double Duration)> Encodes { get; } = new();
    /// <summary>The real copies of a 37.6-minute game: 1080p30 and 720p30.</summary>
    public Func<VodShareQuality, long> SizeFor { get; set; } = q => q == VodShareQuality.P1080 ? 1_315_583_333 : 763_897_802;
    public Func<CancellationToken, Task>? DuringEncode { get; set; }
    public Exception? Error { get; set; }
    /// <summary>The first run fails and a second encoder starts over (beforeRetry runs in between).</summary>
    public bool Restarts { get; set; }
    public int Running;

    public Task<VodEncoderChoice?> SelectAsync(CancellationToken ct) =>
        Task.FromResult<VodEncoderChoice?>(new VodEncoderChoice("ffmpeg.exe", "h264_nvenc"));

    public async Task<VodEncodeResult> EncodeAsync(string sourcePath, string outputPath, VodShareQuality quality, int fps,
        double durationSeconds, IProgress<int>? percent, CancellationToken ct, Func<CancellationToken, Task>? beforeRetry = null)
    {
        lock (Encodes) Encodes.Add((sourcePath, outputPath, quality, fps, durationSeconds));
        Interlocked.Increment(ref Running);
        try
        {
            await File.WriteAllBytesAsync(outputPath, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p'], ct);
            percent?.Report(34);
            if (Restarts)
            {
                File.Delete(outputPath);
                if (beforeRetry is not null) await beforeRetry(ct);
                await File.WriteAllBytesAsync(outputPath, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p'], ct);
            }
            if (DuringEncode is not null) await DuringEncode(ct);
            ct.ThrowIfCancellationRequested();
            if (Error is not null) throw Error;
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref Running);
        }
        return new VodEncodeResult(outputPath, SizeFor(quality), "h264_nvenc", quality, fps,
            VodShareEncoderArgs.VideoKbps(quality, fps, durationSeconds));
    }
}

/// <summary>
/// A repository double without a database: the handler answers the calls a test needs and
/// anything else throws. Keeps the share worker tests off SQLite (and its global pool clears).
/// </summary>
public class RepositoryDouble : DispatchProxy
{
    internal Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (m, _) => throw new NotSupportedException(m.Name);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);

    internal static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = Create<T, RepositoryDouble>();
        ((RepositoryDouble)(object)proxy).Handler = handler;
        return proxy;
    }
}

/// <summary>A worker over a temp folder, a temp recording and doubles for the proxy, ffmpeg and the game row.</summary>
internal sealed class VodShareHarness : IDisposable
{
    public const long GameId = 4321;

    public TestConfigService Config { get; } = new();
    public GameStats? Game { get; set; }
    public string Root { get; }
    public string TempDir { get; }
    public string RecordingsDir { get; }
    public string? Recording { get; set; }
    public FakeVodClient Client { get; } = new();
    public FakeVodEncoder Encoder { get; } = new();
    public FakeClipUpload Upload { get; } = new();
    public FakeMixer Mixer { get; } = new() { ProbeDuration = 2255.4 };
    public LcuLiveState Live { get; } = new();
    public SidecarEventHub Hub { get; } = new();
    public SidecarBackgroundWork Work { get; } = new(NullLogger<SidecarBackgroundWork>.Instance);
    public RemoteClipCleanupStore Cleanup { get; }
    public VodShareStore Store { get; }
    public VodShareWorker Worker { get; }
    public VodDto Snapshot { get; set; } = VodShareMarkersTests.Vod([VodShareMarkersTests.Event(1, "DRAGON", 312, "Dragon", "gold")],
        [VodShareMarkersTests.Bookmark(2, 400, "my note")]);
    public long FreeDisk { get; set; } = 1_000_000_000_000;
    /// <summary>Copies padded to their reserved size (the fake encoder's file is a few bytes, so nothing is written).</summary>
    public List<(string File, long Size)> Pads { get; } = new();

    public VodShareHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "vodshare-" + Guid.NewGuid().ToString("N"));
        TempDir = Path.Combine(Root, "Revu", "ShareTemp");
        RecordingsDir = Path.Combine(Root, "Revu", "Recordings");
        Directory.CreateDirectory(RecordingsDir);
        Recording = Path.Combine(RecordingsDir, "session", "capture.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(Recording)!);
        File.WriteAllBytes(Recording, [1, 2, 3, 4]);
        Cleanup = new RemoteClipCleanupStore(Path.Combine(Root, "Revu", "remote-clip-cleanup.json"), Upload,
            NullLogger<RemoteClipCleanupStore>.Instance);
        Store = new VodShareStore(Path.Combine(Root, "Revu", "vod-shares.json"), NullLogger<VodShareStore>.Instance,
            () => FakeVodClient.Now);
        var games = RepositoryDouble.Create<IGameRepository>((method, args) => method.Name == "GetAsync"
            ? Task.FromResult(args is [long id] && id == GameId ? Game : null)
            : throw new NotSupportedException(method.Name));
        var vod = RepositoryDouble.Create<IVodRepository>((method, _) => throw new NotSupportedException(method.Name));
        Client.EncodeDone = () => Encoder.Encodes.Count > 0 && Volatile.Read(ref Encoder.Running) == 0;
        Worker = new VodShareWorker(Client, Upload, Encoder, Mixer, vod, games,
            (_, _) => Task.FromResult(Snapshot), Config, Live, Store, Cleanup, Hub, Work,
            NullLogger<VodShareWorker>.Instance)
        {
            ResolveRecordingAsync = _ => Task.FromResult<string?>(Recording),
            FreeDiskBytes = _ => FreeDisk,
            PadCopy = (file, size) =>
            {
                lock (Pads) Pads.Add((file, size));
                return true;
            },
            Now = () => FakeVodClient.Now,
            MarkerRetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
            GameCheckInterval = TimeSpan.FromMilliseconds(40),
            TempDirectory = TempDir,
            RecordingsDirectory = RecordingsDir,
        };
    }

    public Task InitAsync(bool signedIn = true, bool started = true, int gameDuration = 1834)
    {
        Game = TestGameStatsFactory.Create(GameId, champion: "Jinx", durationSeconds: gameDuration);
        if (signedIn) SignIn();
        if (started) Worker.Start();
        return Task.CompletedTask;
    }

    public void SignIn()
    {
        Config.Current.RiotSessionToken = "session-token";
        Config.Current.RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
    }

    public void SignOut()
    {
        Config.Current.RiotSessionToken = "";
        Config.Current.RiotSessionExpiresAt = 0;
    }

    public VodShareEntry Entry(string id = "old1234") =>
        new(GameId, id, "https://revu.lol/v/" + id, "1080p", false, 1000, FakeVodClient.Now - 60,
            FakeVodClient.Now + 259_000, true);

    public void Dispose()
    {
        try { Work.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

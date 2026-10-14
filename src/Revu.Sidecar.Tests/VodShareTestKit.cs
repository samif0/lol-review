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

    public Func<string, VodUploadRequest, IProgress<ClipUploadProgress>?, Action<string>?, CancellationToken, Task<VodUploadResult>> OnUpload { get; set; } =
        (_, _, progress, assign, _) =>
        {
            assign?.Invoke("vod1234");
            progress?.Report(new ClipUploadProgress("uploading", 0, 100));
            progress?.Report(new ClipUploadProgress("uploading", 61, 100));
            progress?.Report(new ClipUploadProgress("finishing", 100, 100));
            return Task.FromResult(new VodUploadResult("vod1234", "https://revu.lol/v/vod1234", Now + 259_200));
        };

    public List<(string File, VodUploadRequest Request, bool FileExisted)> Uploads { get; } = new();
    public List<(string Id, string Json)> Markers { get; } = new();
    public Func<int, Exception?> MarkerError { get; set; } = _ => null;

    public Task<VodShareServerStatus> GetVodShareStatusAsync(string sessionToken, CancellationToken ct = default)
    {
        StatusCalls++;
        if (StatusError is not null) return Task.FromException<VodShareServerStatus>(StatusError);
        return Task.FromResult(Status());
    }

    public Task<VodUploadResult> UploadVodAsync(string filePath, string sessionToken, VodUploadRequest request,
        IProgress<ClipUploadProgress>? progress = null, Action<string>? onRemoteIdAssigned = null, CancellationToken ct = default)
    {
        lock (Uploads) Uploads.Add((filePath, request, File.Exists(filePath)));
        return OnUpload(filePath, request, progress, onRemoteIdAssigned, ct);
    }

    public Task PutVodMarkersAsync(string id, string sessionToken, string markersJson, CancellationToken ct = default)
    {
        int attempt;
        lock (Markers)
        {
            Markers.Add((id, markersJson));
            attempt = Markers.Count;
        }
        var error = MarkerError(attempt);
        return error is null ? Task.CompletedTask : Task.FromException(error);
    }
}

/// <summary>Encoder double: writes a small "mp4" and reports the size the test chooses.</summary>
internal sealed class FakeVodEncoder : IVodShareEncoder
{
    public List<(string Source, string Output, VodShareQuality Quality, double Duration)> Encodes { get; } = new();
    public Func<VodShareQuality, long> SizeFor { get; set; } = _ => 1_315_583_333;
    public Func<CancellationToken, Task>? DuringEncode { get; set; }
    public Exception? Error { get; set; }

    public Task<VodEncoderChoice?> SelectAsync(CancellationToken ct) =>
        Task.FromResult<VodEncoderChoice?>(new VodEncoderChoice("ffmpeg.exe", "h264_nvenc"));

    public async Task<VodEncodeResult> EncodeAsync(string sourcePath, string outputPath, VodShareQuality quality,
        double durationSeconds, IProgress<int>? percent, CancellationToken ct)
    {
        lock (Encodes) Encodes.Add((sourcePath, outputPath, quality, durationSeconds));
        await File.WriteAllBytesAsync(outputPath, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p'], ct);
        percent?.Report(34);
        try
        {
            if (DuringEncode is not null) await DuringEncode(ct);
            ct.ThrowIfCancellationRequested();
            if (Error is not null) throw Error;
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
        return new VodEncodeResult(outputPath, SizeFor(quality), "h264_nvenc", quality, 4500);
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
        Worker = new VodShareWorker(Client, Upload, Encoder, Mixer, vod, games,
            (_, _) => Task.FromResult(Snapshot), Config, Live, Store, Cleanup, Hub, Work,
            NullLogger<VodShareWorker>.Instance)
        {
            ResolveRecordingAsync = _ => Task.FromResult<string?>(Recording),
            FreeDiskBytes = _ => FreeDisk,
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

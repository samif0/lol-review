using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>Upload service double: the test decides what each upload does.</summary>
internal sealed class FakeClipUpload : IClipUploadService
{
    public Func<string, IProgress<ClipUploadProgress>?, Action<string>?, CancellationToken, Task<ClipUploadResult>> OnUpload { get; set; } =
        (_, progress, _, _) =>
        {
            progress?.Report(new ClipUploadProgress("uploading", 10, 10));
            return Task.FromResult(new ClipUploadResult("abc1234", "https://revu.lol/abc1234", 99));
        };

    public List<(string File, bool Narrated, int? Duration)> Uploads { get; } = new();
    public List<string> Deleted { get; } = new();
    public List<(string Slug, TranscriptDocument Doc)> Transcripts { get; } = new();
    public bool DeleteResult { get; set; } = true;

    public Task<ClipUploadResult> UploadAsync(string filePath, string sessionToken, string? title = null, string? champion = null,
        int? durationSeconds = null, IProgress<ClipUploadProgress>? progress = null, bool narrated = false,
        Action<string>? onRemoteIdAssigned = null, CancellationToken ct = default)
    {
        lock (Uploads) Uploads.Add((filePath, narrated, durationSeconds));
        return OnUpload(filePath, progress, onRemoteIdAssigned, ct);
    }

    public Task<bool> DeleteAsync(string clipId, string sessionToken, CancellationToken ct = default)
    {
        lock (Deleted) Deleted.Add(clipId);
        return Task.FromResult(DeleteResult);
    }

    /// <summary>When set, every transcript PUT throws this instead of succeeding.</summary>
    public Exception? TranscriptError { get; set; }

    public Task PutTranscriptAsync(string clipId, string sessionToken, TranscriptDocument doc, CancellationToken ct = default)
    {
        if (TranscriptError is not null) return Task.FromException(TranscriptError);
        lock (Transcripts) Transcripts.Add((clipId, doc));
        return Task.CompletedTask;
    }
}

/// <summary>Mixer double: writes a small file as the "render".</summary>
internal sealed class FakeMixer : INarrationMixer
{
    public bool Succeed { get; set; } = true;
    public Func<CancellationToken, Task>? BeforeMix { get; set; }
    public List<MixPlan> Plans { get; } = new();

    public double ProbeDuration { get; set; } = 30;

    public Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct) =>
        Task.FromResult<MediaProbe?>(new MediaProbe(true, ProbeDuration));

    public async Task<bool> MixAsync(MixPlan plan, CancellationToken ct)
    {
        Plans.Add(plan);
        if (BeforeMix is not null) await BeforeMix(ct);
        ct.ThrowIfCancellationRequested();
        if (!Succeed) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputPath)!);
        await File.WriteAllTextAsync(plan.OutputPath, "rendered", ct);
        return true;
    }
}

/// <summary>
/// One temp DB + folders + the narration/share/transcription graph wired with doubles.
/// </summary>
internal sealed class NarrationHarness : IDisposable
{
    public const long GameId = 4321;

    public SidecarWriteScope Scope { get; } = new();
    public string Root { get; }
    public string ClipsDir { get; }
    public string NarrationDir { get; }
    public ClipNarrationRepository Narrations { get; }
    public FakeClipUpload Upload { get; } = new();
    public FakeMixer Mixer { get; } = new();
    public SidecarEventHub Hub { get; } = new();
    public SidecarBackgroundWork Work { get; } = new(NullLogger<SidecarBackgroundWork>.Instance);
    public RemoteClipCleanupStore Cleanup { get; }
    public ClipRetentionGuard Retention { get; }
    public ClipShareWorker Share { get; }
    public NarrationTranscriptionWorker Transcriber { get; }
    public ITranscriptionClient Client { get; set; }
    public INarrationTranscriptChunker Chunker { get; set; }

    /// <summary>How many times the graph asked for the session backup.</summary>
    public int Backups { get; private set; }

    /// <summary>Runs inside each backup request (e.g. to observe the DB state at that moment).</summary>
    public Func<Task>? OnBackup { get; set; }

    private readonly ProxyClient _client = new();
    private readonly ProxyChunker _chunker = new();

    public NarrationHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "narration-" + Guid.NewGuid().ToString("N"));
        ClipsDir = Path.Combine(Root, "clips");
        NarrationDir = Path.Combine(Root, "Narration");
        Directory.CreateDirectory(ClipsDir);
        Directory.CreateDirectory(NarrationDir);
        Narrations = new ClipNarrationRepository(Scope.ConnectionFactory);
        Cleanup = new RemoteClipCleanupStore(Path.Combine(Root, "remote-clip-cleanup.json"), Upload,
            NullLogger<RemoteClipCleanupStore>.Instance);
        Retention = new ClipRetentionGuard(Narrations, Scope.Vod);
        Client = new ThrowingClient();
        Chunker = new ThrowingChunker();
        _client.Inner = () => Client;
        _chunker.Inner = () => Chunker;
        Share = new ClipShareWorker(Scope.Vod, Narrations, Scope.Config, Upload, Retention, Mixer,
            BackupAsync, Hub, Work, Cleanup, NullLogger<ClipShareWorker>.Instance);
        Transcriber = new NarrationTranscriptionWorker(Narrations, Scope.Vod, Scope.Config, _client, _chunker, Mixer, Upload,
            BackupAsync, Hub, Work, NullLogger<NarrationTranscriptionWorker>.Instance)
        {
            Settle = TimeSpan.Zero,
            TransientRetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        };
    }

    private Task BackupAsync()
    {
        Backups++;
        return OnBackup?.Invoke() ?? Task.CompletedTask;
    }

    public async Task InitAsync()
    {
        await Scope.InitializeAsync();
        await Scope.SeedGameAsync(GameId);
    }

    public void SignIn()
    {
        Scope.Config.Current.RiotSessionToken = "session-token";
        Scope.Config.Current.RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
    }

    public NarrationCommands Commands(CancellationToken stopping = default) =>
        new(Scope.Vod, Narrations, Scope.Config, Mixer, Upload, BackupAsync, Share, Transcriber, Cleanup, Hub,
            stopping, NarrationDir, NullLogger.Instance);

    /// <summary>A clip bookmark whose file exists (or not).</summary>
    public async Task<(long Id, string Path)> ClipAsync(int start = 100, int end = 130, bool onDisk = true, string ext = ".mp4")
    {
        var path = System.IO.Path.Combine(ClipsDir, $"Ahri_{start}_{Guid.NewGuid():N}{ext}");
        if (onDisk) await File.WriteAllTextAsync(path, "clip bytes");
        var id = await Scope.Vod.AddBookmarkAsync(GameId, start, "clip", clipStartSeconds: start, clipEndSeconds: end, clipPath: path);
        return (id, path);
    }

    /// <summary>A voice track in the narration folder; returns its lowercase D id.</summary>
    public string Voice(bool validMagic = true, int bytes = 2048)
    {
        var id = Guid.NewGuid().ToString("D");
        var data = new byte[bytes];
        if (validMagic) { data[0] = 0x1A; data[1] = 0x45; data[2] = 0xDF; data[3] = 0xA3; }
        File.WriteAllBytes(System.IO.Path.Combine(NarrationDir, id + ".webm"), data);
        return id;
    }

    public SaveNarrationBody SaveBody(long bookmarkId, string narrationId, int offsetMs = 0) =>
        new(GameId, bookmarkId, narrationId, "audio/webm;codecs=opus", offsetMs, 30_000, 0.8, 1.0, true);

    public void Dispose()
    {
        // Stop the dispatcher loops and drain running jobs before the DB goes away.
        try { Work.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        Scope.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }

    private sealed class ProxyClient : ITranscriptionClient
    {
        public Func<ITranscriptionClient> Inner { get; set; } = null!;
        public Task<TranscriptionChunkResult> TranscribeChunkAsync(byte[] mp3, int offsetMs, int durationMs, string language,
            string token, CancellationToken ct) => Inner().TranscribeChunkAsync(mp3, offsetMs, durationMs, language, token, ct);
    }

    private sealed class ProxyChunker : INarrationTranscriptChunker
    {
        public Func<INarrationTranscriptChunker> Inner { get; set; } = null!;
        public Task<IReadOnlyList<TranscriptChunk>> ExportChunksAsync(string narrationPath, double durationS, string tempDir,
            CancellationToken ct) => Inner().ExportChunksAsync(narrationPath, durationS, tempDir, ct);
    }

    private sealed class ThrowingClient : ITranscriptionClient
    {
        public Task<TranscriptionChunkResult> TranscribeChunkAsync(byte[] mp3, int offsetMs, int durationMs, string language,
            string token, CancellationToken ct) => throw new InvalidOperationException("no client configured");
    }

    private sealed class ThrowingChunker : INarrationTranscriptChunker
    {
        public Task<IReadOnlyList<TranscriptChunk>> ExportChunksAsync(string narrationPath, double durationS, string tempDir,
            CancellationToken ct) => throw new InvalidOperationException("no chunker configured");
    }
}

/// <summary>Collects SSE events published on a hub.</summary>
internal sealed class EventTap : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly System.Threading.Channels.ChannelReader<SidecarEventHub.SidecarEvent> _reader;
    public List<(string Type, JsonElement Payload)> Seen { get; } = new();

    public EventTap(SidecarEventHub hub)
    {
        (_reader, _subscription) = hub.Subscribe();
    }

    /// <summary>Read events until one matches, or fail after the timeout.</summary>
    public async Task<JsonElement> WaitForAsync(string type, Func<JsonElement, bool> match, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        foreach (var seen in Seen.Where(s => s.Type == type))
            if (match(seen.Payload)) return seen.Payload;
        try
        {
            while (await _reader.WaitToReadAsync(cts.Token))
            {
                while (_reader.TryRead(out var evt))
                {
                    var json = JsonSerializer.SerializeToElement(evt.Payload, SidecarJson.CreateOptions());
                    Seen.Add((evt.Type, json));
                    if (evt.Type == type && match(json)) return json;
                }
            }
        }
        catch (OperationCanceledException) { }
        Assert.Fail($"No {type} event matched. Seen: " + string.Join(" | ", Seen.Select(s => s.Type + " " + s.Payload)));
        return default;
    }

    public void Dispose() => _subscription.Dispose();
}

internal static class Replies
{
    public static JsonElement Body(ApiReply reply) =>
        JsonSerializer.SerializeToElement(reply.Body, SidecarJson.CreateOptions());
}

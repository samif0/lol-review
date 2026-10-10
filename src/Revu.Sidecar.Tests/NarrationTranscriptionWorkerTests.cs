using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Automatic transcripts with the proxy client and ffmpeg chunker faked: offsets, the
/// generation compare-and-set, shutdown recovery, error statuses.
/// </summary>
public sealed class NarrationTranscriptionWorkerTests
{
    private sealed class FakeChunker(params TranscriptChunk[] plan) : INarrationTranscriptChunker
    {
        public double? DurationSeen { get; private set; }
        public string TempDir { get; private set; } = "";

        public async Task<IReadOnlyList<TranscriptChunk>> ExportChunksAsync(string narrationPath, double durationS,
            string tempDir, CancellationToken ct)
        {
            DurationSeen = durationS;
            TempDir = tempDir;
            var chunks = new List<TranscriptChunk>();
            foreach (var c in plan)
            {
                var path = Path.Combine(tempDir, Path.GetFileName(c.Path));
                await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, ct);
                chunks.Add(c with { Path = path });
            }
            return chunks;
        }
    }

    private sealed class FakeClient(Func<int, int, CancellationToken, Task<TranscriptionChunkResult>> respond) : ITranscriptionClient
    {
        public List<(int OffsetMs, int DurationMs, string Language)> Calls { get; } = new();

        public Task<TranscriptionChunkResult> TranscribeChunkAsync(byte[] mp3, int offsetMs, int durationMs, string language,
            string token, CancellationToken ct)
        {
            Calls.Add((offsetMs, durationMs, language));
            return respond(offsetMs, durationMs, ct);
        }
    }

    /// <summary>The proxy adds offset_ms to the times; this fake does the same.</summary>
    private static Task<TranscriptionChunkResult> Echo(int offsetMs, int durationMs, CancellationToken _) =>
        Task.FromResult(new TranscriptionChunkResult(
            new[] { new TranscriptSegment(offsetMs / 1000.0 + 0.5, offsetMs / 1000.0 + 2.25, $"line at {offsetMs}") }, "en"));

    private static async Task<(NarrationHarness H, long Bm)> SetupAsync(int offsetMs = 250, bool signedIn = true)
    {
        var h = new NarrationHarness();
        await h.InitAsync();
        if (signedIn) h.SignIn();
        var (bm, clip) = await h.ClipAsync(100, 160);
        await h.Narrations.UpsertAsync(new ClipNarrationRecord(bm, NarrationHarness.GameId, Guid.NewGuid().ToString("D"),
            Path.Combine(h.NarrationDir, "v.webm"), "", clip, offsetMs, 95_000, 0.8, 1, true, TranscriptStatuses.Pending,
            0, "", "", "", "", 0, 0));
        return (h, bm);
    }

    private static readonly TranscriptChunk[] TwoSpeechOneSilent =
    {
        new("chunk_000.mp3", 0, 46.5, false),
        new("chunk_001.mp3", 46.5, 70, true),
        new("chunk_002.mp3", 70, 95, false),
    };

    [Fact]
    public async Task Transcribes_NonSilentChunks_WithClipTimelineOffsets_AndStoresAReadyDocument()
    {
        var (h, bm) = await SetupAsync(offsetMs: 250);
        using (h)
        using (var tap = new EventTap(h.Hub))
        {
            var chunker = new FakeChunker(TwoSpeechOneSilent);
            var client = new FakeClient(Echo);
            h.Chunker = chunker;
            h.Client = client;
            h.Mixer.ProbeDuration = 61.5;
            await h.Scope.Vod.SetBookmarkShareUrlAsync(bm, "https://revu.lol/shared1");

            await h.Transcriber.ProcessAsync(bm, CancellationToken.None);

            Assert.Equal(95.0, chunker.DurationSeen);
            Assert.Equal(new[] { (250, 46_500, "en"), (70_250, 25_000, "en") }, client.Calls);
            var row = (await h.Narrations.GetAsync(bm))!;
            Assert.Equal(TranscriptStatuses.Ready, row.TranscriptStatus);
            var doc = TranscriptDocument.TryParse(row.TranscriptJson)!;
            Assert.Equal("en", doc.Language);
            // The second line starts past the 61.5 s clip: clamped away by Normalize.
            Assert.Equal(new[] { 0.75 }, doc.Segments.Select(s => s.Start));
            Assert.False(Directory.Exists(chunker.TempDir));
            // Pushed to the existing share and recorded.
            Assert.Equal("shared1", h.Upload.Transcripts.Single().Slug);
            Assert.Equal("shared1", row.TranscriptPushedSlug);

            await tap.WaitForAsync("clipNarrationUpdated",
                e => e.GetProperty("transcriptStatus").GetString() == "processing" && e.GetProperty("chunksDone").GetInt32() == 1
                     && e.GetProperty("chunksTotal").GetInt32() == 2);
            await tap.WaitForAsync("clipNarrationUpdated", e => e.GetProperty("transcriptStatus").GetString() == "ready");
        }
    }

    [Fact]
    public async Task AStaleGeneration_DoesNotOverwriteTheNewerNarration()
    {
        var (h, bm) = await SetupAsync();
        using (h)
        {
            h.Chunker = new FakeChunker(TwoSpeechOneSilent);
            h.Client = new FakeClient(async (offset, duration, ct) =>
            {
                // The user re-records while this run is transcribing.
                await h.Narrations.ResetTranscriptAsync(bm, TranscriptStatuses.Pending);
                return await Echo(offset, duration, ct);
            });
            var before = (await h.Narrations.GetAsync(bm))!.TranscriptGeneration;

            await h.Transcriber.ProcessAsync(bm, CancellationToken.None);

            var row = (await h.Narrations.GetAsync(bm))!;
            Assert.Equal(TranscriptStatuses.Pending, row.TranscriptStatus);
            Assert.Equal("", row.TranscriptJson);
            Assert.True(row.TranscriptGeneration > before);
            Assert.Empty(h.Upload.Transcripts);
        }
    }

    [Fact]
    public async Task Cancellation_PutsTheRowBackToPending()
    {
        var (h, bm) = await SetupAsync();
        using (h)
        {
            using var stopping = new CancellationTokenSource();
            var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var chunker = new FakeChunker(TwoSpeechOneSilent);
            h.Chunker = chunker;
            h.Client = new FakeClient(async (_, _, ct) =>
            {
                inFlight.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            });
            var gen = (await h.Narrations.GetAsync(bm))!.TranscriptGeneration;

            var run = h.Transcriber.ProcessAsync(bm, stopping.Token);
            await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TranscriptStatuses.Processing, (await h.Narrations.GetAsync(bm))!.TranscriptStatus);
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            var row = (await h.Narrations.GetAsync(bm))!;
            Assert.Equal(TranscriptStatuses.Pending, row.TranscriptStatus);
            Assert.Equal(gen, row.TranscriptGeneration);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "revu-transcribe-"), chunker.TempDir);
            Assert.False(Directory.Exists(chunker.TempDir));
        }
    }

    [Theory]
    [InlineData(TranscriptionErrorKind.Quota, "quota", "Daily transcript limit reached. Try again tomorrow.", 1)]
    [InlineData(TranscriptionErrorKind.Failed, "failed", "Transcript failed. Try again.", 1)]
    [InlineData(TranscriptionErrorKind.Transient, "failed", "Transcript failed. Try again.", 3)]
    [InlineData(TranscriptionErrorKind.NeedsLogin, "needs_login", "", 1)]
    [InlineData(TranscriptionErrorKind.Unavailable, "failed", "Transcripts are not available yet. Revu will try again later.", 1)]
    public async Task Errors_MapToStatuses(TranscriptionErrorKind kind, string status, string error, int expectedCalls)
    {
        var (h, bm) = await SetupAsync();
        using (h)
        {
            h.Chunker = new FakeChunker(TwoSpeechOneSilent);
            var client = new FakeClient((_, _, _) => throw new TranscriptionException(kind, "proxy said no"));
            h.Client = client;

            await h.Transcriber.ProcessAsync(bm, CancellationToken.None);

            var row = (await h.Narrations.GetAsync(bm))!;
            Assert.Equal(status, row.TranscriptStatus);
            Assert.Equal(error, row.TranscriptError);
            Assert.Equal(expectedCalls, client.Calls.Count);
        }
    }

    [Fact]
    public async Task SignedOut_MarksNeedsLogin_WithoutCallingTheProxy()
    {
        var (h, bm) = await SetupAsync(signedIn: false);
        using (h)
        {
            var client = new FakeClient(Echo);
            h.Client = client;

            await h.Transcriber.ProcessAsync(bm, CancellationToken.None);

            Assert.Equal(TranscriptStatuses.NeedsLogin, (await h.Narrations.GetAsync(bm))!.TranscriptStatus);
            Assert.Empty(client.Calls);
        }
    }

    [Fact]
    public async Task AllSilent_IsReadyWithNoSegments()
    {
        var (h, bm) = await SetupAsync();
        using (h)
        {
            h.Chunker = new FakeChunker(new TranscriptChunk("chunk_000.mp3", 0, 40, true));
            var client = new FakeClient(Echo);
            h.Client = client;

            await h.Transcriber.ProcessAsync(bm, CancellationToken.None);

            var row = (await h.Narrations.GetAsync(bm))!;
            Assert.Equal(TranscriptStatuses.Ready, row.TranscriptStatus);
            Assert.Empty(TranscriptDocument.TryParse(row.TranscriptJson)!.Segments);
            Assert.Empty(client.Calls);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheSessionBackup_RunsBeforeTheFirstWrite(bool signedIn)
    {
        var (h, bm) = await SetupAsync(signedIn: signedIn);
        using (h)
        {
            h.Chunker = new FakeChunker(TwoSpeechOneSilent);
            h.Client = new FakeClient(Echo);
            string? statusAtFirstBackup = null;
            h.OnBackup = async () => statusAtFirstBackup ??= (await h.Narrations.GetAsync(bm))!.TranscriptStatus;

            await h.Transcriber.ProcessAsync(bm, CancellationToken.None);

            // The claim (pending to processing) or the needs_login reset came after the backup.
            Assert.Equal(TranscriptStatuses.Pending, statusAtFirstBackup);
            Assert.Equal(signedIn ? TranscriptStatuses.Ready : TranscriptStatuses.NeedsLogin,
                (await h.Narrations.GetAsync(bm))!.TranscriptStatus);
        }
    }
}

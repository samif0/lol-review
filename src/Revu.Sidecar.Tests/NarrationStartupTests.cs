using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>Startup recovery and sweeps, the remote cleanup queue, and the DI wiring.</summary>
public sealed class NarrationStartupTests
{
    [Fact]
    public async Task Sweep_RecoversProcessing_RemovesOrphans_AndOnlyStaleUnreferencedFiles()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, clip) = await h.ClipAsync();
        var narratedDir = Path.Combine(h.ClipsDir, "narrated");
        Directory.CreateDirectory(narratedDir);

        string Make(string dir, string name, bool old)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "x");
            if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-30));
            return path;
        }

        var liveAudio = Make(h.NarrationDir, "live.webm", old: true);
        var liveRender = Make(narratedDir, "live_narrated.mp4", old: true);
        var staleAudio = Make(h.NarrationDir, "stale.webm", old: true);
        var stalePart = Make(h.NarrationDir, "upload.webm.part", old: true);
        var freshAudio = Make(h.NarrationDir, "fresh.webm", old: false);
        var staleRender = Make(narratedDir, "stale_narrated.mp4.part.mp4", old: true);
        var orphanAudio = Make(h.NarrationDir, "orphan.webm", old: false);
        var orphanRender = Make(narratedDir, "orphan_narrated.mp4", old: false);
        var unrelated = Make(h.NarrationDir, "notes.txt", old: true);

        await h.Narrations.UpsertAsync(new ClipNarrationRecord(bm, NarrationHarness.GameId, "id", liveAudio, liveRender, clip,
            0, 30_000, 0.8, 1, true, TranscriptStatuses.Pending, 0, "", "", "", "", 0, 0));
        await h.Narrations.TryClaimTranscriptAsync(bm, (await h.Narrations.GetAsync(bm))!.TranscriptGeneration);
        // A downgraded 3.13 deleted bookmark 999's row but not its narration.
        await h.Narrations.UpsertAsync(new ClipNarrationRecord(999, NarrationHarness.GameId, "id2", orphanAudio, orphanRender, "",
            0, 30_000, 0.8, 1, true, TranscriptStatuses.Pending, 0, "", "", "", "", 0, 0));
        var backups = 0;

        await NarrationStartup.SweepAsync(h.Narrations, h.NarrationDir, h.ClipsDir, () => { backups++; return Task.CompletedTask; },
            DateTime.UtcNow, NullLogger.Instance);

        Assert.Equal(1, backups);
        Assert.Equal(TranscriptStatuses.Pending, (await h.Narrations.GetAsync(bm))!.TranscriptStatus);
        Assert.Null(await h.Narrations.GetAsync(999));
        Assert.False(File.Exists(orphanAudio));
        Assert.False(File.Exists(orphanRender));
        Assert.True(File.Exists(liveAudio));
        Assert.True(File.Exists(liveRender));
        Assert.True(File.Exists(freshAudio));
        Assert.False(File.Exists(staleAudio));
        Assert.False(File.Exists(stalePart));
        Assert.False(File.Exists(staleRender));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(clip));
    }

    [Fact]
    public async Task Sweep_BacksUpBeforeDeletingOrphans_AndNeverWhenThereIsNothingToDo()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var noop = 0;
        await NarrationStartup.SweepAsync(h.Narrations, h.NarrationDir, h.ClipsDir, () => { noop++; return Task.CompletedTask; },
            DateTime.UtcNow, NullLogger.Instance);
        Assert.Equal(0, noop);

        await h.Narrations.UpsertAsync(new ClipNarrationRecord(999, NarrationHarness.GameId, "id2", "", "", "",
            0, 30_000, 0.8, 1, true, TranscriptStatuses.Ready, 0, "", "", "", "", 0, 0));
        var backups = 0;
        var orphanPresentAtBackup = false;

        await NarrationStartup.SweepAsync(h.Narrations, h.NarrationDir, h.ClipsDir, async () =>
            {
                backups++;
                orphanPresentAtBackup = await h.Narrations.GetAsync(999) is not null;
            },
            DateTime.UtcNow, NullLogger.Instance);

        Assert.Equal(1, backups);
        Assert.True(orphanPresentAtBackup);
        Assert.Null(await h.Narrations.GetAsync(999));
    }

    [Fact]
    public async Task DeleteOrQueue_WithAFiredStopToken_QueuesTheSlug_WithoutCallingTheProxy()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        var gone = await ClipShareLinks.DeleteOrQueueAsync("abc1234", h.Scope.Config, h.Upload, h.Cleanup,
            NullLogger.Instance, stopping.Token);

        Assert.False(gone);
        Assert.Empty(h.Upload.Deleted);
        Assert.Equal(new[] { "abc1234" }, h.Cleanup.Snapshot());
    }

    [Fact]
    public async Task TranscriptCatchUp_PushesReadyTranscriptsTheShareMissed_Once()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, clip) = await h.ClipAsync();
        var render = Path.Combine(h.ClipsDir, "narrated", "r.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(render)!);
        File.WriteAllText(render, "render");
        var json = TranscriptDocument.Normalize(new[] { new TranscriptSegment(1, 2, "gank") }, "en", 30).ToJson();
        await h.Narrations.UpsertAsync(new ClipNarrationRecord(bm, NarrationHarness.GameId, "id", "", render, clip,
            0, 30_000, 0.8, 1, true, TranscriptStatuses.Pending, 0, "", "", "", "", 0, 0));
        var gen = (await h.Narrations.GetAsync(bm))!.TranscriptGeneration;
        await h.Narrations.TryClaimTranscriptAsync(bm, gen);
        await h.Narrations.TrySetTranscriptAsync(bm, gen, TranscriptStatuses.Ready, "en", json, "");
        await h.Scope.Vod.SetBookmarkShareUrlAsync(bm, "https://revu.lol/miss123");
        // A shared clip with no narration render on disk is never given the voice transcript.
        var (plainBm, plainClip) = await h.ClipAsync(200, 230);
        await h.Narrations.UpsertAsync(new ClipNarrationRecord(plainBm, NarrationHarness.GameId, "id2", "",
            Path.Combine(h.ClipsDir, "narrated", "gone.mp4"), plainClip, 0, 30_000, 0.8, 1, true, TranscriptStatuses.Pending,
            0, "", "", "", "", 0, 0));
        var gen2 = (await h.Narrations.GetAsync(plainBm))!.TranscriptGeneration;
        await h.Narrations.TryClaimTranscriptAsync(plainBm, gen2);
        await h.Narrations.TrySetTranscriptAsync(plainBm, gen2, TranscriptStatuses.Ready, "en", json, "");
        await h.Scope.Vod.SetBookmarkShareUrlAsync(plainBm, "https://revu.lol/plain12");
        var backups = 0;
        Task Backup() { backups++; return Task.CompletedTask; }

        // The proxy is older than 3.14 (no transcript route): nothing settles, nothing written.
        h.Upload.TranscriptError = new ClipUploadException("Sharing is temporarily unavailable. Try again in a moment.");
        Assert.Equal(0, await NarrationStartup.PushMissedTranscriptsAsync(h.Narrations, h.Scope.Vod, h.Upload, "tok",
            Backup, CancellationToken.None, NullLogger.Instance));
        Assert.Equal("", (await h.Narrations.GetAsync(bm))!.TranscriptPushedSlug);
        Assert.Equal(0, backups);
        h.Upload.TranscriptError = null;

        var pushed = await NarrationStartup.PushMissedTranscriptsAsync(h.Narrations, h.Scope.Vod, h.Upload, "tok",
            Backup, CancellationToken.None, NullLogger.Instance);
        var again = await NarrationStartup.PushMissedTranscriptsAsync(h.Narrations, h.Scope.Vod, h.Upload, "tok",
            Backup, CancellationToken.None, NullLogger.Instance);

        Assert.Equal((1, 0), (pushed, again));
        Assert.Equal("miss123", h.Upload.Transcripts.Single().Slug);
        Assert.Equal("miss123", (await h.Narrations.GetAsync(bm))!.TranscriptPushedSlug);
        Assert.Equal("", (await h.Narrations.GetAsync(plainBm))!.TranscriptPushedSlug);
        Assert.Equal(1, backups);
    }

    [Fact]
    public async Task TranscriptCatchUp_SettlesAShareTheServerNoLongerHas()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, clip) = await h.ClipAsync();
        var render = Path.Combine(h.ClipsDir, "narrated", "r.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(render)!);
        File.WriteAllText(render, "render");
        await h.Narrations.UpsertAsync(new ClipNarrationRecord(bm, NarrationHarness.GameId, "id", "", render, clip,
            0, 30_000, 0.8, 1, true, TranscriptStatuses.Pending, 0, "", "", "", "", 0, 0));
        var gen = (await h.Narrations.GetAsync(bm))!.TranscriptGeneration;
        await h.Narrations.TryClaimTranscriptAsync(bm, gen);
        await h.Narrations.TrySetTranscriptAsync(bm, gen, TranscriptStatuses.Ready, "en",
            TranscriptDocument.Normalize(new[] { new TranscriptSegment(1, 2, "gank") }, "en", 30).ToJson(), "");
        await h.Scope.Vod.SetBookmarkShareUrlAsync(bm, "https://revu.lol/expired");
        h.Upload.TranscriptError = new ClipUploadException("The shared clip is no longer available.") { Gone = true };

        Assert.Equal(0, await NarrationStartup.PushMissedTranscriptsAsync(h.Narrations, h.Scope.Vod, h.Upload, "tok",
            () => Task.CompletedTask, CancellationToken.None, NullLogger.Instance));

        // Recorded, so later starts do not retry an expired link.
        Assert.Equal("expired", (await h.Narrations.GetAsync(bm))!.TranscriptPushedSlug);
    }

    [Fact]
    public async Task CleanupStore_PersistsAtomically_AndDrainKeepsOnlyFailures()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "cleanup-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "remote-clip-cleanup.json");
        try
        {
            var upload = new FakeClipUpload();
            var store = new RemoteClipCleanupStore(path, upload, NullLogger<RemoteClipCleanupStore>.Instance);
            store.Add("a1");
            store.Add("b2");
            store.Add("a1");
            store.Add(" ");
            store.Remove("missing");

            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                Assert.Equal(new[] { "a1", "b2" }, doc.RootElement.GetProperty("slugs").EnumerateArray().Select(e => e.GetString()));
            Assert.False(File.Exists(path + ".tmp"));

            Assert.Equal(0, await store.DrainAsync("", CancellationToken.None));
            upload.DeleteResult = false;
            Assert.Equal(0, await store.DrainAsync("tok", CancellationToken.None));
            Assert.Equal(2, store.Snapshot().Count);

            upload.DeleteResult = true;
            Assert.Equal(2, await store.DrainAsync("tok", CancellationToken.None));
            Assert.Empty(new RemoteClipCleanupStore(path, upload, NullLogger<RemoteClipCleanupStore>.Instance).Snapshot());

            File.WriteAllText(path, "{not json");
            Assert.Empty(store.Snapshot());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TheNarrationGraph_ResolvesFromTheHostContainer()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Services.AddSidecarServices(isolatedHostTest: true);
        using var app = builder.Build();

        Assert.NotNull(app.Services.GetRequiredService<ClipShareWorker>());
        Assert.NotNull(app.Services.GetRequiredService<NarrationTranscriptionWorker>());
        Assert.NotNull(app.Services.GetRequiredService<RemoteClipCleanupStore>());
        Assert.NotNull(app.Services.GetRequiredService<IClipNarrationRepository>());
        var w = app.Services.GetRequiredService<WriteServices>();
        Assert.IsType<ClipNarrationRepository>(w.ClipNarrations);
        Assert.IsType<ClipRetentionGuard>(w.ClipRetention);
        Assert.IsType<NarrationMixer>(w.NarrationMixer);
        Assert.IsType<TranscriptionClient>(w.Transcription);
        Assert.IsType<NarrationTranscriptChunker>(w.TranscriptChunker);
        Assert.IsType<ClipService>(w.Clips);

        // The optional trailing constructor parameters are actually supplied by MS.DI:
        // eviction sees the protected set, and get_vod sees the narrations (read graph).
        Assert.Same(w.ClipRetention, PrivateField(w.Clips, "_retention"));
        var vodBuilder = app.Services.GetRequiredService<VodSnapshotBuilder>();
        Assert.Same(app.Services.GetRequiredService<IClipNarrationRepository>(), PrivateField(vodBuilder, "_narrations"));
        Assert.NotSame(w.ClipNarrations, PrivateField(vodBuilder, "_narrations"));
    }

    private static object? PrivateField(object target, string name) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(target);
}

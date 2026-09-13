using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class ScanVodsPathTests
{
    [Fact]
    public async Task RetryLinksFinalizedFileAndPublishesOnlyNewGameIdentity()
    {
        using var db = new SidecarWriteScope(); await db.InitializeAsync();
        var game = await db.SeedGameAsync();
        db.Config.Current.AscentFolder = "configured";
        var attempts = 0; var backups = 0; var notifications = new List<long>();
        var scan = new FakeScan(async () =>
        {
            if (++attempts == 1) return new(true, 0, 1, "still finalizing");
            await db.Vod.TryLinkUnownedVodAsync(game.GameId, "external.mp4");
            return new(true, 1, 1, "linked") { LinkedGameIds = [game.GameId] };
        });
        var work = new SidecarBackgroundWork(NullLogger<SidecarBackgroundWork>.Instance);
        await AscentRecordingScan.RunWithRetryAsync(scan, db.Config, db.Vod, work,
            () => { backups++; return Task.CompletedTask; }, notifications.Add, NullLogger.Instance,
            game.GameId, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        Assert.Equal(2, attempts); Assert.Equal(2, backups);
        Assert.Equal(new[] { game.GameId }, notifications);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new VodScanResult(true, 1, 1, "linked")
            { LinkedGameIds = [game.GameId] }, SidecarJson.CreateOptions()));
        Assert.Equal(new[] { "ok", "matched", "recordingCount", "message" }, json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task DisconnectAndShutdownStopRetriesWithoutScanningOrWriting()
    {
        using var db = new SidecarWriteScope(); await db.InitializeAsync();
        var attempts = 0;
        var scan = new FakeScan(() => { attempts++; return Task.FromResult(new VodScanResult(true, 0, 0, "")); });
        var work = new SidecarBackgroundWork(NullLogger<SidecarBackgroundWork>.Instance);
        Task Run() => AscentRecordingScan.RunWithRetryAsync(scan, db.Config, db.Vod, work,
            () => throw new Exception("No write may begin"), _ => throw new Exception("No event"), NullLogger.Instance,
            delays: [TimeSpan.FromHours(1)]);
        await Run(); Assert.Equal(0, attempts);
        db.Config.Current.AscentFolder = "configured";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(work.TryRun("test Ascent retry", async () => { entered.SetResult(); await Run(); }));
        await entered.Task;
        await work.StopAsync(CancellationToken.None);
        Assert.False(work.HasOutstandingWork); Assert.False(work.ShutdownIncomplete); Assert.Equal(0, attempts);
    }

    private sealed class FakeScan(Func<Task<VodScanResult>> run) : IVodService
    {
        public Task<VodScanResult> ScanAsync(CancellationToken cancellationToken = default, IReadOnlySet<long>? reservedGameIds = null) => run();
        public Task<List<VodRecordingInfo>> FindRecordingsAsync(string? folder = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string? MatchRecordingToGame(GameStats game, IReadOnlyList<VodRecordingInfo> recordings, IReadOnlySet<string>? excludePaths = null) => throw new NotSupportedException();
        public Task<bool> TryLinkRecordingAsync(GameStats game, string? folder = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> AutoMatchRecordingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

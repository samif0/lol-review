using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The recordings folder size limit: oldest finished sessions go first, only between games,
/// and nothing that is newest, unfinished, pending its match, sharing or open is deleted.
/// One "GB" is 1000 bytes here so the folders stay tiny.
/// </summary>
public sealed class RecordingStoragePrunerTests : IDisposable
{
    private const long Unit = 1000;
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "Revu.Prune.Tests", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_testRoot, "Revu", "Recordings");
    private readonly SidecarWriteScope _db = new();
    private RecordingRegistrationService _registrations = null!;
    private long? _sharing;
    private bool _inGame;
    private int _backups;

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_testRoot, recursive: true); } catch { }
    }

    private async Task InitAsync(int limitGb)
    {
        Directory.CreateDirectory(Root);
        await _db.InitializeAsync();
        _db.Config.Current.RecordingsMaxSizeGb = limitGb;
        _registrations = new(Root, new(_db.ConnectionFactory), () => Task.CompletedTask,
            NullLogger<RecordingRegistrationService>.Instance);
    }

    private RecordingStoragePruner Pruner() => new(Root, _db.Config, _registrations, new(_db.ConnectionFactory),
        () => _sharing, () => _inGame, () => { _backups++; return Task.CompletedTask; },
        NullLogger<RecordingStoragePruner>.Instance, Unit);

    // A session folder like the host writes: session.json plus one video of `bytes`.
    private async Task<(Guid Id, string Video)> SessionAsync(int minutes, long bytes, string state = "saved", long? gameId = null, bool link = true)
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(Root, id.ToString("D"));
        Directory.CreateDirectory(folder);
        var video = Path.Combine(folder, "recording.mp4");
        await File.WriteAllBytesAsync(video, new byte[bytes]);
        await File.WriteAllTextAsync(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(new
        {
            sessionId = id.ToString("D"), state, gameId, startedAt = Base.AddMinutes(minutes).ToString("O"),
        }));
        if (gameId is long game && link)
        {
            await _db.SeedGameAsync(game);
            await _db.Vod.LinkVodAsync(game, video, bytes, 10);
        }
        return (id, video);
    }

    [Fact]
    public async Task WithoutALimit_NothingIsDeleted()
    {
        await InitAsync(0);
        var (_, video) = await SessionAsync(0, 5000, gameId: 1);
        var result = await Pruner().PruneAsync();
        Assert.Equal("no-limit", result.Status);
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task OverTheLimit_DeletesOldestFinishedSessionsAndTheirLinksOnly()
    {
        await InitAsync(6); // 6000 bytes; each session is 2500 plus its session.json
        var oldest = await SessionAsync(0, 2500, gameId: 1);
        var middle = await SessionAsync(60, 2500, gameId: 2);
        var newest = await SessionAsync(120, 2500, gameId: 3);

        var result = await Pruner().PruneAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.Deleted);
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldest.Video)));
        Assert.Null(await _db.Vod.GetVodAsync(1));
        Assert.True(File.Exists(middle.Video));
        Assert.Equal(middle.Video, (await _db.Vod.GetVodAsync(2))!.FilePath);
        Assert.True(File.Exists(newest.Video));
        Assert.Equal(1, _backups);
        Assert.True(result.FolderBytes <= 6 * Unit);
        Assert.NotNull(await _db.Games.GetAsync(1)); // the match itself stays
    }

    [Fact]
    public async Task TheNewestSessionIsKeptEvenWhenItAloneIsOverTheLimit()
    {
        await InitAsync(1);
        var only = await SessionAsync(0, 5000, gameId: 1);
        var result = await Pruner().PruneAsync();
        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(only.Video));
    }

    [Fact]
    public async Task DuringAGame_NothingIsDeleted()
    {
        await InitAsync(1);
        var old = await SessionAsync(0, 5000, gameId: 1);
        await SessionAsync(60, 100, gameId: 2);
        _inGame = true;
        Assert.Equal("game-in-progress", (await Pruner().PruneAsync()).Status);
        Assert.True(File.Exists(old.Video));
    }

    [Theory]
    [InlineData("recording")]
    [InlineData("stopping")]
    [InlineData("finalizing")]
    [InlineData("awaiting-match")]
    [InlineData(null)]
    public async Task UnfinishedOrUnknownSessionsAreNeverDeleted(string? state)
    {
        await InitAsync(1);
        var unfinished = await SessionAsync(0, 5000, state: state!, gameId: 1);
        if (state is null) File.Delete(Path.Combine(Path.GetDirectoryName(unfinished.Video)!, "session.json"));
        await SessionAsync(60, 100, gameId: 2);
        await Pruner().PruneAsync();
        Assert.True(File.Exists(unfinished.Video));
        Assert.NotNull(await _db.Vod.GetVodAsync(1));
    }

    [Fact]
    public async Task TheSharingGameAndAnOpenFileAreSkipped_AndTheNextOldestGoes()
    {
        await InitAsync(4); // 4000 bytes
        var shared = await SessionAsync(0, 3000, gameId: 1);
        var open = await SessionAsync(10, 3000, gameId: 2);
        var next = await SessionAsync(20, 3000, gameId: 3);
        await SessionAsync(30, 100, gameId: 4);
        _sharing = 1;

        PruneResult result;
        using (new FileStream(open.Video, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = new(await Pruner().PruneAsync());

        Assert.Equal(1, result.Value.Deleted);
        Assert.True(File.Exists(shared.Video));
        Assert.True(File.Exists(open.Video));
        Assert.False(File.Exists(next.Video));
        Assert.Null(await _db.Vod.GetVodAsync(3));
        Assert.NotNull(await _db.Vod.GetVodAsync(2));
    }

    [Fact]
    public async Task APendingMatchReceiptIsNeverDeleted()
    {
        await InitAsync(1);
        var pending = await SessionAsync(0, 32, gameId: 9001, link: false);
        // A finished partial whose receipt still waits for its match row.
        var registration = new RecordingRegistration(pending.Id, 9001, pending.Video, 32, 10,
            DateTimeOffset.UtcNow.AddSeconds(-10), false, -30);
        Assert.Equal("pending-match", (await _registrations.RegisterAsync(registration)).Status);
        await SessionAsync(60, 5000, gameId: 2);
        await SessionAsync(120, 10, gameId: 3);

        var result = await Pruner().PruneAsync();

        Assert.True(File.Exists(pending.Video));
        Assert.Equal("pending-match", (await _registrations.GetAsync(pending.Id))!.Status);
        Assert.Equal(1, result.Deleted); // game 2's session went instead
    }

    [Fact]
    public async Task ADeletedSessionsReceiptGoesWithIt()
    {
        await InitAsync(1);
        var old = await SessionAsync(0, 32, gameId: 1, link: false);
        await _db.SeedGameAsync(1);
        var registration = new RecordingRegistration(old.Id, 1, old.Video, 32, 10,
            DateTimeOffset.UtcNow.AddSeconds(-10), true, -30);
        Assert.Equal("linked", (await _registrations.RegisterAsync(registration)).Status);
        await File.AppendAllTextAsync(old.Video + ".pad", new string('x', 2000));
        await SessionAsync(60, 50, gameId: 2);

        Assert.Equal(1, (await Pruner().PruneAsync()).Deleted);
        Assert.Null(await _registrations.GetAsync(old.Id));
        Assert.Null(await _db.Vod.GetVodAsync(1));
    }

    [Fact]
    public void MeasureCountsOnlySessionFolders()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".registrations"));
        File.WriteAllBytes(Path.Combine(Root, ".registrations", "x.json"), new byte[70]);
        File.WriteAllBytes(Path.Combine(Root, "stray.mp4"), new byte[90]);
        var session = Path.Combine(Root, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(session);
        File.WriteAllBytes(Path.Combine(session, "a.mp4"), new byte[40]);
        Assert.Equal(40, RecordingStoragePruner.MeasureFolderBytes(Root));
    }

    private readonly record struct PruneResult(RecordingPruneResult Value);
}

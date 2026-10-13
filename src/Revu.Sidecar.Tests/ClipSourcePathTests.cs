using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// /api/clip/extract and /api/pattern/moment/note clip from the recording Revu linked to
/// the game, never a renderer-supplied path, and never from a network path.
/// </summary>
public sealed class ClipSourcePathTests
{
    [WindowsFact]
    public async Task TheLinkedLocalRecording_IsTheOnlyClipSource()
    {
        using var db = new SidecarWriteScope();
        await db.InitializeAsync();
        var game = await db.SeedGameAsync();
        var recording = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "game.mp4");
        await File.WriteAllTextAsync(recording, "video");

        Assert.Null(await SidecarEndpoints.ResolveLocalVodPathAsync(db.Vod, game.GameId));

        await db.Vod.LinkVodAsync(game.GameId, recording);
        Assert.Equal(recording, await SidecarEndpoints.ResolveLocalVodPathAsync(db.Vod, game.GameId));

        File.Delete(recording);
        Assert.Null(await SidecarEndpoints.ResolveLocalVodPathAsync(db.Vod, game.GameId));
    }

    [WindowsFact]
    public async Task ANetworkRecordingPath_IsNeverOpened()
    {
        using var db = new SidecarWriteScope();
        await db.InitializeAsync();
        var game = await db.SeedGameAsync();
        await db.Vod.LinkVodAsync(game.GameId, @"\\attacker.invalid\share\game.mp4");

        Assert.Null(await SidecarEndpoints.ResolveLocalVodPathAsync(db.Vod, game.GameId));
    }
}

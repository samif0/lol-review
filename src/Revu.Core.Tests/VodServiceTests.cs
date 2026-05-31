using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

public sealed class VodServiceTests
{
    [Fact]
    public async Task TryLinkRecordingAsync_ReusesKnownVodFolder_WhenConfigFolderIsEmpty()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var recordingsFolder = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", Guid.NewGuid().ToString("N"), "Ascent");
        Directory.CreateDirectory(recordingsFolder);

        try
        {
            var existingStart = new DateTime(2026, 5, 30, 18, 2, 0, DateTimeKind.Local);
            var existingPath = CreateRecording(recordingsFolder, "05-30-2026-18-02.mp4", existingStart, 1838);
            var existingGame = TestGameStatsFactory.Create(
                gameId: 1001,
                timestamp: ToUnixSeconds(existingStart.AddSeconds(39)),
                durationSeconds: 1838);
            await scope.Games.SaveAsync(existingGame);
            await scope.Vod.LinkVodAsync(existingGame.GameId, existingPath, new FileInfo(existingPath).Length);

            var targetStart = new DateTime(2026, 5, 30, 20, 36, 0, DateTimeKind.Local);
            var targetPath = CreateRecording(recordingsFolder, "05-30-2026-20-36.mp4", targetStart, 1917);
            var targetGame = TestGameStatsFactory.Create(
                gameId: 1002,
                champion: "Sivir",
                timestamp: ToUnixSeconds(targetStart.AddSeconds(14)),
                durationSeconds: 1917);
            await scope.Games.SaveAsync(targetGame);

            var service = new VodService(
                scope.Games,
                scope.Vod,
                new TestConfigService(new AppConfig { AscentFolder = "" }),
                NullLogger<VodService>.Instance);

            var linked = await service.TryLinkRecordingAsync(targetGame);

            Assert.True(linked);
            var vod = await scope.Vod.GetVodAsync(targetGame.GameId);
            Assert.NotNull(vod);
            Assert.Equal(targetPath, vod.FilePath);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(recordingsFolder)!, recursive: true); }
            catch { }
        }
    }

    private static string CreateRecording(string folder, string fileName, DateTime start, int durationSeconds)
    {
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTime(path, start.AddSeconds(durationSeconds + 20));
        return path;
    }

    private static long ToUnixSeconds(DateTime localTime) =>
        new DateTimeOffset(localTime).ToUnixTimeSeconds();
}

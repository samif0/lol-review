using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The Patterns page marks a trend ready for a back-to-back review with the same rule as
/// the nudge, and lists the moments that review plays: only the ones saved since the last
/// review, oldest first, leaving out any with nothing to play.
/// </summary>
public sealed class PatternsSnapshotReadinessTests
{
    private sealed class TempVods : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"revu-ready-vods-{Guid.NewGuid():N}");
        public TempVods() { Directory.CreateDirectory(_dir); }
        public string For(long gameId)
        {
            var path = Path.Combine(_dir, $"{gameId}.mp4");
            if (!File.Exists(path)) File.WriteAllBytes(path, new byte[] { 0 });
            return path;
        }
        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }
    }

    // The recording's timing companion: game time at the video's first frame.
    private static Task StartRecordingAtAsync(string vodPath, long gameId, double gameTimeAtVideoStart) =>
        File.WriteAllTextAsync(vodPath + RecordingTimeline.Suffix, JsonSerializer.Serialize(
            new RecordingTiming(1, gameId, Path.GetFileName(vodPath), gameTimeAtVideoStart),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static Task<PatternsSnapshotDto> BuildAsync(SidecarWriteScope scope) =>
        new PatternsSnapshotBuilder(scope.Evidence, NullLogger<PatternsSnapshotBuilder>.Instance).BuildAsync();

    private static async Task ExecAsync(SidecarWriteScope scope, string sql, params (string Name, object Value)[] args)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AReadyTrend_PlaysItsWatchableMomentsOldestFirst_AndCollectionsNeverDo()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Track the jungler", "macro");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Games 9101..9104, oldest first. 9102 lost its recording and kept no clip.
        for (var i = 0; i < 4; i++)
        {
            var gameId = 9101 + i;
            await scope.SeedGameAsync(gameId: gameId, timestamp: now - (4 - i) * 3600);
            if (gameId != 9102) await scope.Vod.LinkVodAsync(gameId, vods.For(gameId));
            await scope.Vod.AddBookmarkAsync(gameId, 300, "Walked into fog", objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        }
        await scope.Vod.AddBookmarkAsync(9104, 900, "Again", objectiveId: objectiveId, quality: EvidencePolarities.Bad);

        var snapshot = await BuildAsync(scope);

        var trend = Assert.Single(snapshot.Patterns, card => card.ReviewMode == "trend");
        Assert.True(trend.ReadyToReview);
        Assert.Equal(new[] { 1, 3, 4, 5 }, trend.ReviewMomentOrdinals);
        Assert.All(trend.ReviewMomentOrdinals!, ordinal =>
            Assert.True(trend.Moments[ordinal - 1].HasVod || trend.Moments[ordinal - 1].HasClip));
        var saved = Assert.Single(snapshot.Patterns, card => card.ReviewMode == "saved");
        Assert.False(saved.ReadyToReview);
        Assert.Empty(saved.ReviewMomentOrdinals!);
    }

    [Fact]
    public async Task AfterAReview_OnlyNewMomentsAreQueued_OnceThereAreEnough()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward before crossing", "macro");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 5; i++)
        {
            var gameId = 9201 + i;
            await scope.SeedGameAsync(gameId: gameId, timestamp: now - (5 - i) * 3600);
            await scope.Vod.LinkVodAsync(gameId, vods.For(gameId));
            await scope.Vod.AddBookmarkAsync(gameId, 300, "Crossed without a ward", objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        }
        var key = $"{PatternConstants.KindBadObjectiveEvidence}:obj{objectiveId}";
        await scope.Evidence.MarkPatternReviewedAsync(key, PatternConstants.KindBadObjectiveEvidence, 5);
        // The review happened yesterday; games 9201 and 9202 were saved before it.
        await ExecAsync(scope, "UPDATE pattern_reviews SET reviewed_at = @at", ("@at", now - 86_400));
        await ExecAsync(scope, "UPDATE vod_bookmarks SET created_at = @at WHERE game_id IN (9201, 9202)", ("@at", now - 90_000));

        var snapshot = await BuildAsync(scope);

        var trend = Assert.Single(snapshot.Patterns, card => card.ReviewMode == "trend");
        Assert.False(trend.ReadyToReview, "three new moments are not enough");
        Assert.Empty(trend.ReviewMomentOrdinals!);

        await scope.Vod.AddBookmarkAsync(9201, 800, "Again", objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        trend = Assert.Single((await BuildAsync(scope)).Patterns, card => card.ReviewMode == "trend");
        Assert.True(trend.ReadyToReview);
        Assert.Equal(new[] { 2, 4, 5, 6 }, trend.ReviewMomentOrdinals);
        Assert.Equal(9201, trend.Moments[1].GameId);
        Assert.Equal(800, trend.Moments[1].StartTimeSeconds + 15);
    }

    [Fact]
    public async Task AMomentThatEndsBeforeItsRecordingStarts_IsNotWatchable_AndDoesNotCount()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Track the jungler", "macro");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 4; i++)
        {
            var gameId = 9301 + i;
            await scope.SeedGameAsync(gameId: gameId, timestamp: now - (4 - i) * 3600, durationSeconds: 1800);
            await scope.Vod.LinkVodAsync(gameId, vods.For(gameId));
            await scope.Vod.AddBookmarkAsync(gameId, 300, "Walked into fog", objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        }
        // Game 9304's recording began ten minutes in: its 5:00 bookmark (4:45 to 5:15) is not in it.
        await StartRecordingAtAsync(vods.For(9304), 9304, 600);

        var trend = Assert.Single((await BuildAsync(scope)).Patterns, card => card.ReviewMode == "trend");
        var early = Assert.Single(trend.Moments, m => m.GameId == 9304);
        Assert.False(early.HasVod);
        Assert.Equal("", early.VodPath);
        Assert.False(trend.ReadyToReview, "three moments that can play are not enough");
        Assert.Equal(1, trend.UnwatchableMomentCount);

        // A later moment in the same recording plays, and the trend is ready without the early one.
        await scope.Vod.AddBookmarkAsync(9304, 900, "Again", objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        trend = Assert.Single((await BuildAsync(scope)).Patterns, card => card.ReviewMode == "trend");
        Assert.True(trend.ReadyToReview);
        Assert.Equal(new[] { 1, 2, 3, 5 }, trend.ReviewMomentOrdinals);
        var late = trend.Moments[4];
        Assert.Equal(885, late.StartTimeSeconds);
        Assert.True(late.HasVod);
        Assert.Equal(600, late.GameTimeAtVideoStart);
    }
}

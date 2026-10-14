using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// GET /api/patterns/nudge: the quiet notice behind the Patterns nav count and the Home
/// pill. It waits for a habit (four watchable mistakes across three games), never counts
/// saved collections, stays quiet for 48 hours after any review and while the player is
/// in champ select or a game, and clears as soon as the review is finished.
/// </summary>
public sealed class PatternNudgeReaderTests
{
    private sealed class TempVods : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"revu-nudge-vods-{Guid.NewGuid():N}");
        public TempVods() { Directory.CreateDirectory(_dir); }
        public string File(string name)
        {
            var path = Path.Combine(_dir, name);
            if (!System.IO.File.Exists(path)) System.IO.File.WriteAllBytes(path, new byte[] { 0 });
            return path;
        }
        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PatternNudgeReader Reader(SidecarWriteScope scope, LcuLiveState? live = null,
        TimeProvider? time = null, Func<string, bool>? fileExists = null) => new(
        scope.Evidence, live ?? new LcuLiveState(), NullLogger<PatternNudgeReader>.Instance, time, fileExists);

    /// <summary>One objective with To-improve bookmarks: momentsPerGame in each of `games`
    /// recent games, each game with a recording on disk unless told otherwise.</summary>
    private static async Task<long> SeedTrendAsync(SidecarWriteScope scope, TempVods vods, string title, long firstGameId,
        int games, int momentsPerGame = 1, bool withRecordings = true, string quality = EvidencePolarities.Bad)
    {
        var objectiveId = await scope.Objectives.CreateAsync(title, "macro");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var g = 0; g < games; g++)
        {
            var gameId = firstGameId + g;
            await scope.SeedGameAsync(gameId: gameId, timestamp: now - (g + 1) * 3600);
            if (withRecordings) await scope.Vod.LinkVodAsync(gameId, vods.File($"{gameId}.mp4"));
            for (var m = 0; m < momentsPerGame; m++)
                await scope.Vod.AddBookmarkAsync(gameId, 300 + m * 120, "Walked into fog",
                    objectiveId: objectiveId, quality: quality);
        }
        return objectiveId;
    }

    private static string TrendKey(long objectiveId) => $"{PatternConstants.KindBadObjectiveEvidence}:obj{objectiveId}";

    [Fact]
    public async Task FourWatchableMistakesAcrossThreeGames_Nudge_WithCountsButNoFilePaths()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var ready = await SeedTrendAsync(scope, vods, "Ward before crossing", 7101, games: 3);
        await scope.Vod.AddBookmarkAsync(7101, 900, "Again", objectiveId: ready, quality: EvidencePolarities.Bad);

        var nudge = await Reader(scope).ReadAsync();

        Assert.True(nudge.Notify);
        Assert.Equal(1, nudge.ReadyCount);
        Assert.Equal(TrendKey(ready), nudge.PatternKey);
        Assert.Equal("Ward before crossing", nudge.Title);
        Assert.Equal(4, nudge.MomentCount);
        Assert.Equal(3, nudge.GameCount);
        Assert.Equal("", nudge.QuietReason);
        Assert.Equal("", nudge.ErrorText);
        Assert.DoesNotContain(".mp4", JsonSerializer.Serialize(nudge), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThreeAcrossThreeGames_OrFourAcrossTwo_DoNotNudge()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        await SeedTrendAsync(scope, vods, "Three across three", 7201, games: 3);
        await SeedTrendAsync(scope, vods, "Four across two", 7301, games: 2, momentsPerGame: 2);

        var nudge = await Reader(scope).ReadAsync();

        Assert.False(nudge.Notify);
        Assert.Equal(0, nudge.ReadyCount);
        Assert.Equal("", nudge.PatternKey);
    }

    [Fact]
    public async Task MomentsWithNothingToPlay_DoNotCount_ButAKeptClipDoes()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var objectiveId = await SeedTrendAsync(scope, vods, "Track the jungler", 7401, games: 3);
        // A fourth game whose recording was pruned: its vod_files row outlives the file.
        await scope.SeedGameAsync(gameId: 7404, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600);
        await scope.Vod.LinkVodAsync(7404, Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}.mp4"));
        await scope.Vod.AddBookmarkAsync(7404, 300, "Gone", objectiveId: objectiveId, quality: EvidencePolarities.Bad);

        Assert.False((await Reader(scope).ReadAsync()).Notify);

        // A clip kept from that game plays without its recording.
        await scope.Vod.AddBookmarkAsync(7404, 600, "Kept", clipStartSeconds: 590, clipEndSeconds: 610,
            clipPath: vods.File("kept-clip.mp4"), objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        var nudge = await Reader(scope).ReadAsync();
        Assert.True(nudge.Notify);
        Assert.Equal(4, nudge.MomentCount);
        Assert.Equal(4, nudge.GameCount);
    }

    [Fact]
    public async Task AMomentBeforeItsRecordingStarts_DoesNotCount()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        await SeedTrendAsync(scope, vods, "Track the jungler", 7451, games: 4);
        // The fourth game's recording began ten minutes in; its 5:00 bookmark is not in it.
        var late = vods.File("7454.mp4");
        await File.WriteAllTextAsync(late + Revu.Core.Services.RecordingTimeline.Suffix, JsonSerializer.Serialize(
            new Revu.Core.Services.RecordingTiming(1, 7454, Path.GetFileName(late), 600),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var nudge = await Reader(scope).ReadAsync();

        Assert.False(nudge.Notify, "three moments that can play are not enough");
        Assert.Equal(0, nudge.ReadyCount);
    }

    [Fact]
    public async Task SavedCollectionsNeverNudge()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        await SeedTrendAsync(scope, vods, "Good examples", 7501, games: 4, momentsPerGame: 2, quality: EvidencePolarities.Good);
        await SeedTrendAsync(scope, vods, "Neutral notes", 7601, games: 4, momentsPerGame: 2, quality: EvidencePolarities.Neutral);

        var nudge = await Reader(scope).ReadAsync();

        Assert.False(nudge.Notify);
        Assert.Equal(0, nudge.ReadyCount);
    }

    [Fact]
    public async Task FinishingTheReview_ClearsTheNudgeAtOnce_AndAnyReviewKeepsItQuietFor48Hours()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var first = await SeedTrendAsync(scope, vods, "Track the jungler", 7701, games: 4, momentsPerGame: 2);
        var second = await SeedTrendAsync(scope, vods, "Ward before crossing", 7801, games: 4);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var reader = Reader(scope, time: clock);

        var before = await reader.ReadAsync();
        Assert.True(before.Notify);
        Assert.Equal(2, before.ReadyCount);
        Assert.Equal(TrendKey(first), before.PatternKey);   // high severity leads

        await scope.Evidence.MarkPatternReviewedAsync(TrendKey(first), PatternConstants.KindBadObjectiveEvidence, 8);
        var after = await reader.ReadAsync();
        Assert.False(after.Notify);
        Assert.Equal(PatternNudgeReader.QuietRecentReview, after.QuietReason);
        Assert.Equal(1, after.ReadyCount);
        Assert.Equal(TrendKey(second), after.PatternKey);

        clock.Now = clock.Now.AddHours(PatternConstants.NudgeQuietHours - 1);
        Assert.False((await reader.ReadAsync()).Notify);

        clock.Now = clock.Now.AddHours(2);
        var later = await reader.ReadAsync();
        Assert.True(later.Notify);
        Assert.Equal(1, later.ReadyCount);
        Assert.Equal(TrendKey(second), later.PatternKey);
    }

    [Fact]
    public async Task ChampSelectAndGamesHoldTheNudge_AndADodgeReleasesIt()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        await SeedTrendAsync(scope, vods, "Track the jungler", 7901, games: 4);
        var live = new LcuLiveState();
        var reader = Reader(scope, live);

        live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        var champSelect = await reader.ReadAsync();
        Assert.False(champSelect.Notify);
        Assert.Equal(PatternNudgeReader.QuietLive, champSelect.QuietReason);
        Assert.Equal(1, champSelect.ReadyCount);

        // A dodge keeps the session key for the next lobby, but the player is free again.
        live.CancelChampSelect();
        Assert.NotNull(live.SessionKey);
        Assert.True((await reader.ReadAsync()).Notify);

        live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        live.SetGameInProgress(true);
        Assert.False(live.IsInChampSelect);
        var inGame = await reader.ReadAsync();
        Assert.False(inGame.Notify);
        Assert.Equal(PatternNudgeReader.QuietLive, inGame.QuietReason);

        live.TakeForGameEnd();
        Assert.True((await reader.ReadAsync()).Notify);

        live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        live.SetLcuConnected(false);
        Assert.True((await reader.ReadAsync()).Notify, "a closed client is not champ select");
    }

    [Fact]
    public async Task FileChecksAreRememberedForAMinute()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        await SeedTrendAsync(scope, vods, "Track the jungler", 8001, games: 4, withRecordings: false);
        for (var gameId = 8001; gameId <= 8004; gameId++)
            await scope.Vod.LinkVodAsync(gameId, $@"C:\synthetic\{gameId}.mp4");
        var checks = 0;
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var reader = Reader(scope, time: clock, fileExists: _ => { checks++; return true; });

        Assert.True((await reader.ReadAsync()).Notify);
        var first = checks;
        Assert.True(first >= 4);

        clock.Now = clock.Now.AddSeconds(30);
        Assert.True((await reader.ReadAsync()).Notify);
        Assert.Equal(first, checks);

        clock.Now = clock.Now.Add(PatternNudgeReader.ProbeLifetime);
        Assert.True((await reader.ReadAsync()).Notify);
        Assert.Equal(2 * first, checks);
    }

    [Fact]
    public async Task AFailedCheck_NeverNudges()
    {
        using var scope = new SidecarWriteScope();   // no schema: every read fails

        var nudge = await Reader(scope).ReadAsync();

        Assert.False(nudge.Notify);
        Assert.Equal(PatternNudgeReader.FailedText, nudge.ErrorText);
    }
}

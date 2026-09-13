using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

public sealed class VodOwnershipTests
{
    [Fact]
    public async Task GameEndAndReviewReadsDoNotDiscoverFilesFromExistingRecordingFolders()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var media = new MediaFolder();
        var known = media.Create("05-30-2026-18-02.mp4");
        var candidate = media.Create("05-30-2026-20-36.mp4");
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(1001));
        await scope.Vod.LinkVodAsync(1001, known, 4, 1800);
        var config = new TestConfigService();
        var game = TestGameStatsFactory.Create(1002);
        Assert.Equal(1002, await GameProcessor(scope, config).ProcessGameEndAsync(new(game)));
        var review = Review(scope, config);
        Assert.False((await review.LoadAsync(1002))!.HasVod);
        Assert.False((await review.CheckVodAsync(1002)).HasVod);
        Assert.Null(await scope.Vod.GetVodAsync(1002));
        Assert.Equal(known, (await scope.Vod.GetVodAsync(1001))!.FilePath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(candidate));

        // An explicit attachment remains supported and does not infer a match.
        await scope.Vod.LinkVodAsync(1002, candidate, 4, 1800);
        Assert.True((await review.CheckVodAsync(1002)).HasVod);
        Assert.True((await review.LoadAsync(1002))!.HasVod);
    }

    [Fact]
    public async Task ExistingVodFileRowAndBookmarksSurviveGameAndReviewUpdates()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var media = new MediaFolder();
        var path = media.Create("existing-recording.mp4");
        var game = TestGameStatsFactory.Create(1001);
        await scope.Games.SaveAsync(game);
        await scope.Vod.LinkVodAsync(1001, path, 4, 1800);
        await scope.Vod.AddBookmarkAsync(1001, 600, "existing note");
        var original = await scope.Vod.GetVodAsync(1001);
        var config = new TestConfigService();
        Assert.Equal(1001, await GameProcessor(scope, config).ProcessGameEndAsync(new(game)));
        var review = Review(scope, config);
        Assert.True((await review.LoadAsync(1001))!.HasVod);
        Assert.Equal(1, (await review.CheckVodAsync(1001)).BookmarkCount);
        Assert.Equal(original, await scope.Vod.GetVodAsync(1001));
        Assert.Equal("existing note", Assert.Single(await scope.Vod.GetBookmarksAsync(1001)).Note);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ManualAttachmentPreservesFileOwnershipAndAllowsExplicitReplacement()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var media = new MediaFolder();
        var first = media.Create("first.mp4");
        var second = media.Create("second.mp4");
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(1001));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(1002));
        await scope.Vod.LinkVodAsync(1001, first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Vod.LinkVodAsync(1002, first));
        await scope.Vod.LinkVodAsync(1001, second);
        Assert.Equal(second, (await scope.Vod.GetVodAsync(1001))!.FilePath);
        Assert.Null(await scope.Vod.GetVodAsync(1002));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    private static GameService GameProcessor(TestDatabaseScope scope, IConfigService config) => new(
        scope.Games, scope.SessionLog, new RulesRepository(scope.ConnectionFactory), scope.GameEvents,
        scope.DerivedEvents, config, NullLogger<GameService>.Instance);
    private static ReviewWorkflowService Review(TestDatabaseScope scope, IConfigService config) => new(
        scope.Games, scope.ConceptTags, scope.Vod, scope.SessionLog, scope.Objectives, scope.ReviewDrafts,
        scope.MatchupNotes, scope.Evidence, config, new NullCoachSidecarNotifier(), NullLogger<ReviewWorkflowService>.Instance);

    private sealed class MediaFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.VodOwnership.Tests", Guid.NewGuid().ToString("N"));
        public MediaFolder() => Directory.CreateDirectory(_root);
        public string Create(string name)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            return path;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

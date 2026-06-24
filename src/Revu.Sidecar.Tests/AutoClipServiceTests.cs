using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Integration tests for <see cref="AutoClipService"/> against a real temp SQLite DB,
/// with a FAKE <see cref="IClipService"/> so ffmpeg never runs. Verifies the toggle
/// gate, the bookmark + evidence + practiced writes per kept event, idempotency, and
/// the "no VOD / no events" reasons.
/// </summary>
public sealed class AutoClipServiceTests
{
    // Records the requested ranges and returns a throwaway path (no ffmpeg).
    private sealed class FakeClipService : IClipService
    {
        public List<(int Start, int End)> Calls { get; } = new();
        public Task<string?> FindFfmpegAsync() => Task.FromResult<string?>("ffmpeg");
        public Task<string?> ExtractClipAsync(string vodPath, int startS, int endS, string champion, string? outputFolder = null)
        {
            Calls.Add((startS, endS));
            return Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), $"clip_{startS}_{endS}.mp4"));
        }
        public Task EnforceFolderSizeLimitAsync(string folder, long maxSizeBytes) => Task.CompletedTask;
    }

    private static async Task<(SidecarWriteScope scope, FakeClipService clips, AutoClipService svc, string vodPath, long gameId)>
        SetupAsync(bool toggleOn, string token = "DEATH")
    {
        var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 4242, durationSeconds: 1800);

        // A real file on disk so GetVodPathsAsync + File.Exists pass.
        var vodPath = Path.Combine(Path.GetTempPath(), $"revu-autoclip-{System.Guid.NewGuid():N}.mp4");
        await File.WriteAllTextAsync(vodPath, "not really a video");
        await scope.Vod.LinkVodAsync(game.GameId, vodPath, fileSize: 10, durationSeconds: 1800);

        // An active objective tracking the token.
        var objId = await scope.Objectives.CreateWithPhasesAsync(
            "Review every death", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetEventTokensForObjectiveAsync(objId, new[] { token });

        scope.Config.Current.AutoClipObjectivesEnabled = toggleOn;
        scope.Config.Current.ClipsFolder = Path.GetTempPath();

        var clips = new FakeClipService();
        var svc = new AutoClipService(
            scope.Config, scope.Games, scope.Vod, new GameEventsRepository(scope.ConnectionFactory),
            scope.Objectives, scope.Evidence, clips, NullLogger<AutoClipService>.Instance);

        return (scope, clips, svc, vodPath, game.GameId);
    }

    private static GameEvent Ev(string type, int t) =>
        new() { EventType = type, GameTimeS = t, Details = "{}" };

    [Fact]
    public async Task ToggleOff_ProducesNothing()
    {
        var (scope, clips, svc, vodPath, gameId) = await SetupAsync(toggleOn: false);
        using (scope)
        {
            var events = new GameEventsRepository(scope.ConnectionFactory);
            await events.SaveEventsAsync(gameId, new[] { Ev("DEATH", 600) });

            var result = await svc.ClipObjectiveEventsAsync(gameId, null);

            Assert.Equal(0, result.Created);
            Assert.Equal("disabled", result.Reason);
            Assert.Empty(clips.Calls);
            Assert.Empty(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
            File.Delete(vodPath);
        }
    }

    [Fact]
    public async Task HappyPath_WritesBookmarkEvidenceAndMarksPracticed()
    {
        var (scope, clips, svc, vodPath, gameId) = await SetupAsync(toggleOn: true);
        using (scope)
        {
            var events = new GameEventsRepository(scope.ConnectionFactory);
            // Two deaths far enough apart to both survive the min-gap.
            await events.SaveEventsAsync(gameId, new[] { Ev("DEATH", 600), Ev("DEATH", 900), Ev("KILL", 700) });

            var result = await svc.ClipObjectiveEventsAsync(gameId, null);

            Assert.Equal(2, result.Created);          // both deaths; the kill is untracked
            Assert.Equal(2, clips.Calls.Count);
            // Buffer math: first death 600 → 570-615.
            Assert.Contains((570, 615), clips.Calls);

            var evidence = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
            Assert.Equal(2, evidence.Count);
            Assert.All(evidence, e => Assert.Equal(EvidenceKinds.Clip, e.SourceKind));
            Assert.All(evidence, e => Assert.StartsWith($"autoclip:{gameId}:", e.SourceKey));

            var bookmarks = await scope.Vod.GetBookmarksAsync(gameId);
            Assert.Equal(2, bookmarks.Count);

            // The objective is marked practiced for this game.
            var perGame = await scope.Objectives.GetGameObjectivesAsync(gameId);
            Assert.Contains(perGame, r => r.Practiced);

            File.Delete(vodPath);
        }
    }

    [Fact]
    public async Task SecondRun_IsIdempotent()
    {
        var (scope, clips, svc, vodPath, gameId) = await SetupAsync(toggleOn: true);
        using (scope)
        {
            var events = new GameEventsRepository(scope.ConnectionFactory);
            await events.SaveEventsAsync(gameId, new[] { Ev("DEATH", 600), Ev("DEATH", 900) });

            var first = await svc.ClipObjectiveEventsAsync(gameId, null);
            Assert.Equal(2, first.Created);

            var second = await svc.ClipObjectiveEventsAsync(gameId, null);
            Assert.Equal(0, second.Created);          // dedupe — nothing new

            Assert.Equal(2, (await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true)).Count);
            Assert.Equal(2, (await scope.Vod.GetBookmarksAsync(gameId)).Count);
            File.Delete(vodPath);
        }
    }

    [Fact]
    public async Task NoVod_ReturnsNoVodReason()
    {
        var (scope, clips, svc, vodPath, gameId) = await SetupAsync(toggleOn: true);
        using (scope)
        {
            // Remove the linked recording from disk so File.Exists fails.
            File.Delete(vodPath);
            await scope.Vod.UnlinkVodAsync(gameId);

            var events = new GameEventsRepository(scope.ConnectionFactory);
            await events.SaveEventsAsync(gameId, new[] { Ev("DEATH", 600) });

            var result = await svc.ClipObjectiveEventsAsync(gameId, null);

            Assert.Equal(0, result.Created);
            Assert.Equal("no_vod", result.Reason);
            Assert.Empty(clips.Calls);
        }
    }

    [Fact]
    public async Task NoTiedEvents_ReturnsNoEventsReason()
    {
        var (scope, clips, svc, vodPath, gameId) = await SetupAsync(toggleOn: true);
        using (scope)
        {
            var events = new GameEventsRepository(scope.ConnectionFactory);
            // Only a kill — the objective tracks DEATH, so nothing ties.
            await events.SaveEventsAsync(gameId, new[] { Ev("KILL", 600) });

            var result = await svc.ClipObjectiveEventsAsync(gameId, null);

            Assert.Equal(0, result.Created);
            Assert.Equal("no_events", result.Reason);
            Assert.Empty(clips.Calls);
            File.Delete(vodPath);
        }
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Sidecar;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// POST /api/review/skipped, the Match library's Skip, bulk Skip and Unskip. The route hands
/// its handler the write graph's session log and backup guard, so these tests call the
/// handler over the write-capable factory and read back through the read-only one, as the
/// sidecar does (no HTTP host, house style). A skipped game reads Skipped (never Reviewed),
/// leaves To review, Home's Ready to review and the Review page's Next game, and is counted
/// as skipped in the library strip; Unskip makes it wait for review again.
/// </summary>
public sealed class LibrarySkipTests
{
    private static long Today(int offset) =>
        Math.Max(DateTimeOffset.Now.ToUnixTimeSeconds() - 600, new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds()) + offset;

    [Fact]
    public async Task Skip_LeavesToReview_ReadsSkipped_KeepsTheDraft_AndUnskipBringsTheGameBack()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        // Seeded games have no session row yet, like a legacy import: the skip must still hold.
        await scope.SeedGameAsync(gameId: 8401, win: true, timestamp: Today(1));
        await scope.SeedGameAsync(gameId: 8402, win: false, timestamp: Today(2));
        await scope.SeedGameAsync(gameId: 8403, win: true, timestamp: Today(3));
        await scope.Games.UpdateReviewAsync(8403, new GameReview { Rating = 1, Notes = "Reviewed the lane." });
        await scope.ReviewDrafts.UpsertAsync(new ReviewDraft { GameId = 8401, ReviewNotes = "Half a thought." });
        var (write, games) = Graphs(scope);
        var backups = 0;

        var (status, reply) = Read(await SidecarEndpoints.SetReviewSkippedAsync(new ReviewSkippedBody([8401, 8402, 9999]), write,
            () => { backups++; return Task.CompletedTask; }, SidecarJson.CreateOptions(), NullLogger.Instance));

        Assert.Equal(200, status);
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.True(reply.GetProperty("skipped").GetBoolean());
        Assert.Equal(new long[] { 8401, 8402 }, reply.GetProperty("gameIds").EnumerateArray().Select(id => id.GetInt64()));
        Assert.Equal(1, backups);

        var builder = Builder(scope, games);
        Assert.Empty((await builder.BuildAsync(view: "queue")).Items);
        var today = await builder.BuildAsync(view: "today");
        var skipped = today.Items.Single(row => row.GameId == 8401);
        Assert.True(skipped.IsSkipped);
        Assert.False(skipped.HasReview);   // a skip is not a review
        Assert.Equal("Skipped", skipped.ReviewStateText);
        Assert.Equal("Review", skipped.PrimaryAction);   // it can still be reviewed
        Assert.Equal(new GamesTotalsDto(Games: 3, Wins: 2, Reviewed: 1, Skipped: 2), today.Totals);
        var history = await builder.BuildAsync(view: "history");
        Assert.Equal(new GamesTotalsDto(Games: 3, Wins: 2, Reviewed: 1, Skipped: 2), history.Totals);
        Assert.Equal(history.Totals!.Skipped, history.Items.Count(row => row.IsSkipped));
        Assert.NotNull(await scope.ReviewDrafts.GetAsync(8401));   // kept, so an Undo loses nothing

        (status, reply) = Read(await SidecarEndpoints.SetReviewSkippedAsync(new ReviewSkippedBody([8402], Skipped: false), write,
            () => Task.CompletedTask, SidecarJson.CreateOptions(), NullLogger.Instance));

        Assert.Equal(200, status);
        Assert.False(reply.GetProperty("skipped").GetBoolean());
        Assert.Equal(new long[] { 8402 }, (await builder.BuildAsync(view: "queue")).Items.Select(row => row.GameId));
        var after = await builder.BuildAsync(view: "history");
        Assert.Equal("Unreviewed", after.Items.Single(row => row.GameId == 8402).ReviewStateText);
        Assert.False(after.Items.Single(row => row.GameId == 8402).IsSkipped);
        Assert.Equal(1, after.Totals!.Skipped);
    }

    [Fact]
    public async Task AReviewedGame_WithAStaleSkipFlag_ReadsReviewed_AndIsNeverCountedAsSkipped()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(gameId: 8501, timestamp: Today(1));
        await scope.SessionLog.SetSkippedAsync(new long[] { 8501 }, skipped: true);
        await scope.Games.UpdateReviewAsync(8501, new GameReview { Rating = 1, Notes = "Reviewed after all." });

        var history = await Builder(scope, Graphs(scope).Games).BuildAsync(view: "history");

        var row = Assert.Single(history.Items);
        Assert.True(row.HasReview);
        Assert.False(row.IsSkipped);
        Assert.Equal("Reviewed", row.ReviewStateText);
        Assert.Equal(new GamesTotalsDto(Games: 1, Wins: 1, Reviewed: 1, Skipped: 0), history.Totals);
    }

    [Fact]
    public async Task ASkippedGame_LeavesHomesReadyToReview_AndTheReviewPagesNextGame()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        foreach (var (id, at) in new[] { (8601L, 1), (8602L, 2), (8603L, 3) })
        {
            await scope.SeedGameAsync(gameId: id, timestamp: Today(at));
            await scope.SessionLog.LogGameAsync(id, "Ahri", win: true);
        }
        var home = new DashboardSnapshotBuilder(
            scope.SessionLog, scope.Objectives, scope.Vod, scope.Evidence, scope.DeathClassifications,
            new RulesRepository(scope.ConnectionFactory), scope.Games, scope.CoachingStints, scope.Config,
            NullLogger<DashboardSnapshotBuilder>.Instance);
        var review = new ReviewSnapshotBuilder(
            scope.Games, scope.Games, scope.Objectives, scope.Prompts, scope.SessionLog,
            scope.Evidence, new GameEventsRepository(scope.ConnectionFactory),
            scope.DeathClassifications, scope.MatchupNotes, scope.ConceptTags, scope.Vod,
            scope.Config, scope.ReviewDrafts, NullLogger<ReviewSnapshotBuilder>.Instance);
        Assert.Equal(3, (await home.BuildAsync()).Unreviewed.Count);

        await scope.SessionLog.SetSkippedAsync(new long[] { 8603, 8602 }, skipped: true);

        var dashboard = await home.BuildAsync();
        Assert.Equal(1, dashboard.Unreviewed.Count);
        Assert.Equal(new long[] { 8601 }, dashboard.Unreviewed.Items.Select(item => item.GameId));
        Assert.Equal(3, dashboard.Stats.TotalGames);   // the games still count as played
        var reviewPage = await review.BuildAsync(8603);
        Assert.Equal(8601, reviewPage.NextUnreviewedGameId);
        Assert.Equal(1, reviewPage.UnreviewedRemaining);

        await scope.SessionLog.SetSkippedAsync(new long[] { 8603 }, skipped: false);
        Assert.Equal(2, (await home.BuildAsync()).Unreviewed.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"skipped":true}""")]
    [InlineData("""{"gameIds":[],"skipped":true}""")]
    [InlineData("""{"gameIds":[0],"skipped":true}""")]
    [InlineData("""{"gameIds":[8701,-3],"skipped":false}""")]
    public async Task AMissingEmptyOrInvalidSelection_IsA400_BeforeTheBackupOrTheDatabase(string? json)
    {
        // A scope with no schema: any database call would throw.
        using var scope = new SidecarWriteScope();
        var backups = 0;
        var body = json is null ? null : JsonSerializer.Deserialize<ReviewSkippedBody>(json, SidecarJson.CreateOptions());

        var (status, reply) = Read(await SidecarEndpoints.SetReviewSkippedAsync(body!, new SessionLogRepository(scope.ConnectionFactory),
            () => { backups++; return Task.CompletedTask; }, SidecarJson.CreateOptions(), NullLogger.Instance));

        Assert.Equal(400, status);
        Assert.StartsWith("gameIds required", reply.GetProperty("error").GetString());
        Assert.Equal(0, backups);
    }

    [Fact]
    public async Task ASelectionLargerThanOneRequest_IsA400_SoThePageSendsItInParts()
    {
        using var scope = new SidecarWriteScope();
        var ids = Enumerable.Range(1, SidecarEndpoints.MaxSkippedGameIds + 1).Select(id => (long)id).ToList();

        var (status, _) = Read(await SidecarEndpoints.SetReviewSkippedAsync(new ReviewSkippedBody(ids),
            new SessionLogRepository(scope.ConnectionFactory), () => Task.CompletedTask, SidecarJson.CreateOptions(), NullLogger.Instance));

        Assert.Equal(400, status);
        Assert.Equal(500, SidecarEndpoints.MaxSkippedGameIds);   // games-skip.mjs SKIP_BATCH sends this many at most
    }

    // The route's two graphs: writes through the write-capable factory, reads through the
    // physically read-only one, both on the scope's database file.
    private static (SessionLogRepository Write, GameRepository Games) Graphs(SidecarWriteScope scope) => (
        new SessionLogRepository(new WriteSqliteConnectionFactory(
            NullLogger<WriteSqliteConnectionFactory>.Instance, dbPath: scope.DatabasePath)),
        new GameRepository(new ReadOnlySqliteConnectionFactory(
            NullLogger<ReadOnlySqliteConnectionFactory>.Instance, scope.DatabasePath), new NoopBackupService()));

    private static GamesSnapshotBuilder Builder(SidecarWriteScope scope, GameRepository games) =>
        new(games, scope.Vod, scope.Objectives, scope.Config, NullLogger<GamesSnapshotBuilder>.Instance);

    private static (int Status, JsonElement Body) Read(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return (status, JsonSerializer.SerializeToElement(value, SidecarJson.CreateOptions()));
    }
}

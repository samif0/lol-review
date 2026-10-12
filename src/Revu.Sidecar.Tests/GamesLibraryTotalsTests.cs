using Microsoft.Extensions.Logging;
using Revu.Core.Models;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The Match library stat strip describes the whole selected view, not the page
/// of rows loaded so far, and a failed read is an error, never "no games".
/// </summary>
public sealed class GamesLibraryTotalsTests
{
    private static GamesSnapshotBuilder Builder(SidecarWriteScope scope, ILogger<GamesSnapshotBuilder>? logger = null) =>
        new(scope.Games, scope.Vod, scope.Objectives, scope.Config, logger ?? new RecordingLogger());

    [Fact]
    public async Task HistoryTotals_CoverEveryVisibleGame_NotJustTheLoadedPage()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        // 35 games: ten wins (8001-8010), four reviewed (8001-8004), one remake
        // (8035) and one hidden win (8005).
        for (var id = 8001L; id <= 8035; id++)
            await scope.SeedGameAsync(gameId: id, win: id <= 8010, timestamp: 1_700_000_000 + id);
        for (var id = 8001L; id <= 8004; id++)
            await scope.Games.UpdateReviewAsync(id, new GameReview { Notes = "Reviewed the lane." });
        await scope.Games.SetRemakeVerdictAsync(8035, true, false, "eog");
        await scope.Games.SetHiddenAsync(8005, true);

        var builder = Builder(scope);
        foreach (var page in new[] { 0, 1 })
        {
            var dto = await builder.BuildAsync(view: "history", page: page);
            Assert.Null(dto.ErrorText);
            Assert.Equal(new GamesTotalsDto(Games: 33, Wins: 9, Reviewed: 4), dto.Totals);
            Assert.Equal(34, dto.TotalCount); // listed rows: the remake is shown, the hidden game is not
        }
        Assert.Equal(30, (await builder.BuildAsync(view: "history", page: 0)).Items.Count);
    }

    [Fact]
    public async Task HistoryReviewedCount_IgnoresWhitespaceOnlyReviewText_LikeTheRowLabel()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(gameId: 8201, win: false, timestamp: 1_700_000_001);
        await scope.SeedGameAsync(gameId: 8202, win: false, timestamp: 1_700_000_002);
        await scope.Games.UpdateReviewAsync(8201, new GameReview { Notes = "Real note." });
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE games SET mistakes = ' \t\r\n ', went_well = '   ' WHERE game_id = 8202";
            await cmd.ExecuteNonQueryAsync();
        }

        var history = await Builder(scope).BuildAsync(view: "history");
        Assert.False(history.Items.Single(row => row.GameId == 8202).HasReview);
        Assert.Equal(new GamesTotalsDto(Games: 2, Wins: 0, Reviewed: 1), history.Totals);
        Assert.Equal(history.Totals!.Reviewed, history.Items.Count(row => row.HasReview));
    }

    [Fact]
    public async Task SingleShotViewTotals_ExcludeRemakes_AndMatchTheRowLabels()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        // Today, even just after midnight.
        var start = Math.Max(DateTimeOffset.Now.ToUnixTimeSeconds() - 600, new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds());
        await scope.SeedGameAsync(gameId: 9001, win: true, timestamp: start);
        await scope.SeedGameAsync(gameId: 9002, win: false, timestamp: start + 1);
        await scope.SeedGameAsync(gameId: 9003, win: true, timestamp: start + 2);
        await scope.Games.UpdateReviewAsync(9001, new GameReview { Notes = "Good trade timing." });
        await scope.Games.SetRemakeVerdictAsync(9003, true, false, "eog");

        var today = await Builder(scope).BuildAsync(view: "today");
        Assert.Equal(3, today.Items.Count);
        Assert.Equal(new GamesTotalsDto(Games: 2, Wins: 1, Reviewed: 1), today.Totals);
        Assert.Equal(today.Totals!.Reviewed, today.Items.Count(row => !row.IsRemake && row.HasReview));

        var queue = await Builder(scope).BuildAsync(view: "queue");
        Assert.Equal(new GamesTotalsDto(Games: 1, Wins: 0, Reviewed: 0), queue.Totals);
    }

    [Fact]
    public async Task RemoveFromHistory_IsASoftHide_AndUndoRestoresTheGameAndItsCounts()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(gameId: 8101, win: true, timestamp: 1_700_000_001);
        await scope.SeedGameAsync(gameId: 8102, win: false, timestamp: 1_700_000_002);
        await scope.Games.UpdateReviewAsync(8101, new GameReview { Notes = "Kept my notes." });
        var builder = Builder(scope);

        await scope.Games.SetHiddenAsync(8101, true);
        var hidden = await builder.BuildAsync(view: "history");
        Assert.DoesNotContain(hidden.Items, row => row.GameId == 8101);
        Assert.Equal(new GamesTotalsDto(Games: 1, Wins: 0, Reviewed: 0), hidden.Totals);
        Assert.Equal("Kept my notes.", (await scope.Games.GetAsync(8101))!.ReviewNotes);

        await scope.Games.SetHiddenAsync(8101, false);
        var restored = await builder.BuildAsync(view: "history");
        Assert.Contains(restored.Items, row => row.GameId == 8101 && row.HasReview);
        Assert.Equal(new GamesTotalsDto(Games: 2, Wins: 1, Reviewed: 1), restored.Totals);
    }

    [Fact]
    public async Task AFailedRead_IsLoggedAsAWarningAndShownAsAnError_NotAsAnEmptyLibrary()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(gameId: 9101);
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE games RENAME TO games_unreadable";
            await cmd.ExecuteNonQueryAsync();
        }

        var logger = new RecordingLogger();
        foreach (var view in new[] { "history", "queue", "today", "vod" })
        {
            var dto = await Builder(scope, logger).BuildAsync(view: view);
            Assert.False(string.IsNullOrWhiteSpace(dto.ErrorText));
            Assert.DoesNotContain("—", dto.ErrorText);
            Assert.Null(dto.Totals);
            Assert.Empty(dto.Items);
        }
        Assert.Equal(4, logger.Levels.Count(level => level == LogLevel.Warning));
    }

    private sealed class RecordingLogger : ILogger<GamesSnapshotBuilder>
    {
        public List<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}

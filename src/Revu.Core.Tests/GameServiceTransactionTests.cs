using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

/// <summary>
/// Game end saves the games row, its remake verdict and its session row as one unit, so
/// a failure never strands a game without a session row (dropped from session stats) and
/// a re-run of the same capture saves it whole.
/// </summary>
public sealed class GameServiceTransactionTests
{
    [Fact]
    public async Task SessionRowFails_NothingIsSaved_AndARerunSavesTheGameWhole()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await ExecAsync(scope, """
            CREATE TRIGGER fail_session BEFORE INSERT ON session_log
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);
        var svc = BuildService(scope);
        var request = new ProcessGameEndRequest(Game(7501), MentalRating: 7, PreGameMood: 4);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.ProcessGameEndAsync(request));

        Assert.Null(await scope.Games.GetAsync(7501));
        Assert.Null(await scope.Games.GetRemakeVerdictAsync(7501));

        await ExecAsync(scope, "DROP TRIGGER fail_session");
        Assert.Equal(7501, await svc.ProcessGameEndAsync(request));

        Assert.NotNull(await scope.Games.GetAsync(7501));
        var entry = await scope.SessionLog.GetEntryAsync(7501);
        Assert.NotNull(entry);
        Assert.Equal(4, entry!.PreGameMood);
        Assert.False((await scope.Games.GetRemakeVerdictAsync(7501))!.IsRemake);
    }

    [Fact]
    public async Task RemakeVerdictFails_TheGameAndItsSessionRowStillSave()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await ExecAsync(scope, """
            CREATE TRIGGER fail_verdict BEFORE INSERT ON game_remakes
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        var saved = await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7502)));

        Assert.Equal(7502, saved);
        Assert.NotNull(await scope.Games.GetAsync(7502));
        Assert.NotNull(await scope.SessionLog.GetEntryAsync(7502));
        Assert.Null(await scope.Games.GetRemakeVerdictAsync(7502));
    }

    private static GameStats Game(long id)
    {
        var stats = TestGameStatsFactory.Create(id, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1800);
        stats.EndedInEarlySurrender = false;
        stats.EarlySurrenderSource = "eog";
        return stats;
    }

    private static GameService BuildService(TestDatabaseScope scope) => new(
        scope.ConnectionFactory, scope.Games, scope.SessionLog, new RulesRepository(scope.ConnectionFactory),
        scope.GameEvents, scope.DerivedEvents, new TestConfigService(), NullLogger<GameService>.Instance);

    private static async Task ExecAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

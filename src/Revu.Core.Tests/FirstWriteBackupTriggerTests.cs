using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The session backup is taken right before the first real write, not on every launch:
/// an Ascent scan that links nothing and a corrections sweep whose orphan still has no
/// twin take no backup, and when they do write, the backup sees the state before it.
/// </summary>
public sealed class FirstWriteBackupTriggerTests
{
    private static readonly DateTime Start = new(2026, 9, 11, 17, 25, 0, DateTimeKind.Local);

    private static GameEvent Ev(long gameId, string type, int t, string details = "{}") =>
        new() { GameId = gameId, EventType = type, GameTimeS = t, Details = details };

    private static async Task ExecAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // What a backup taken at this moment would read: a private connection outside the
    // app's shared cache, so it sees committed data only.
    private static long CommittedScalar(TestDatabaseScope scope, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = scope.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static async Task<long> DanglingRetimeAsync(TestDatabaseScope scope, long gameId)
    {
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));
        await scope.GameEvents.SaveEventsAsync(gameId, [Ev(gameId, "DEATH", 812, "{\"killer\":\"Lee Sin\"}")]);
        using var conn = scope.OpenConnection();
        var death = (await EventCorrectionSql.LoadRowsAsync(conn, null, gameId)).Single();
        await new EventCorrectionsRepository(scope.ConnectionFactory).SaveAsync(new EventCorrectionRequest(gameId,
            Guid.NewGuid().ToString("D"), CorrectionOps.Retime,
            new EventCorrectionSubject(death.EventKey, death.Id, "DEATH", 812), new EventPatch(null, 815, null, null), "Clock drift"));
        await scope.GameEvents.DeleteEventsAsync(gameId);
        return gameId;
    }

    [Fact]
    public async Task CorrectionsSweep_WithAnOrphanThatStillHasNoTwin_TakesNoBackup()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await DanglingRetimeAsync(scope, 7301);
        var sweep = new EventCorrectionSweep(scope.ConnectionFactory);
        Assert.True(await sweep.CountPendingAsync() > 0);

        var backups = 0;
        await sweep.RunAsync(beforeFirstWrite: () => { backups++; return Task.CompletedTask; });
        await sweep.RunAsync(beforeFirstWrite: () => { backups++; return Task.CompletedTask; });

        Assert.Equal(0, backups);
    }

    [Fact]
    public async Task CorrectionsSweep_BacksUpOnce_RightBeforeItsFirstChange()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        const long gameId = 7302;
        await DanglingRetimeAsync(scope, gameId);
        // The row comes back already keyed, so the only write is the orphan resolution.
        await ExecAsync(scope, $"INSERT INTO game_events (game_id, event_type, game_time_s, details, event_key) " +
                               $"VALUES ({gameId}, 'DEATH', 812, '{{\"killer\":\"Lee Sin\"}}', 'det:DEATH:812:Lee Sin')");

        var backups = 0;
        long seenAtBackup = -1;
        var result = await new EventCorrectionSweep(scope.ConnectionFactory).RunAsync(beforeFirstWrite: () =>
        {
            backups++;
            seenAtBackup = CommittedScalar(scope, $"SELECT game_time_s FROM game_events WHERE game_id = {gameId}");
            return Task.CompletedTask;
        });

        Assert.Equal(1, result.OrphansResolved);
        Assert.Equal(1, backups);
        Assert.Equal(812, seenAtBackup);
        Assert.Equal(815, CommittedScalar(scope, $"SELECT game_time_s FROM game_events WHERE game_id = {gameId}"));
    }

    [Fact]
    public async Task AscentScan_BacksUpOnlyRightBeforeItLinksARecording()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var root = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "ascent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var game = TestGameStatsFactory.Create(1001);
            game.Timestamp = new DateTimeOffset(Start).ToUnixTimeSeconds();
            game.GameDuration = 1800;
            await scope.Games.SaveAsync(game);
            var service = new VodService(scope.Games, scope.Vod,
                new TestConfigService(new AppConfig { AscentFolder = root }), NullLogger<VodService>.Instance);
            var backups = 0;
            Task Backup() { backups++; return Task.CompletedTask; }

            // Nothing to link: no backup.
            Assert.Equal(0, (await service.ScanAsync(CancellationToken.None, null, Backup)).Matched);
            Assert.Equal(0, backups);

            var file = Path.Combine(root, "09-11-2026-17-25.mp4");
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            File.SetLastWriteTime(file, Start.AddMinutes(30));
            var linkedAtBackup = true;
            var result = await service.ScanAsync(CancellationToken.None, null, async () =>
            {
                await Backup();
                linkedAtBackup = await scope.Vod.GetVodAsync(1001) is not null;
            });

            Assert.Equal(1, result.Matched);
            Assert.Equal(1, backups);
            Assert.False(linkedAtBackup, "the backup must come before the link");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

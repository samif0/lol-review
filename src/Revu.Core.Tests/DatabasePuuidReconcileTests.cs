using Microsoft.Data.Sqlite;
using Revu.Core.Models;

namespace Revu.Core.Tests;

/// <summary>
/// Back-catalog account-scope repair (DatabaseInitializer.ReconcileGamePuuidAsync).
///
/// Games captured before 3.1.6 (or by a stale sidecar) were saved with the
/// UUID-shaped LCU localPlayer.puuid (e.g. 07f45763-…) instead of the encrypted
/// Riot PUUID our login stores in config. SessionLogRepository.CurrentAccountFilter
/// / GameRepository string-compare games.puuid to the configured PUUID, so a stale
/// LCU id strands every such game behind the filter ("GAMES 0 even though I played
/// N"). The per-game reconcile in GameService only fixes games captured AFTER the
/// fix; this one-time startup sweep repairs the history.
///
/// These tests pin the scope decision (2026-06-22): re-stamp non-empty mismatched
/// rows ONLY, leave legacy puuid='' rows alone, no-op when logged out, idempotent.
/// </summary>
public sealed class DatabasePuuidReconcileTests
{
    private const string LcuPuuid = "07f45763-5e2d-5b2b-9fef-6f410a1e98c4";  // UUID-shaped (LCU)
    private const string RiotPuuid = "MI8rRotPr5sxilCFBJkVD4ih2-real-encrypted-puuid"; // 78ch-ish config value
    private const string ForeignPuuid = "someoneElsesEncryptedPuuid-not-mine";
    private const string Today = "2026-06-22";

    [Fact]
    public async Task Reconcile_ReStampsLcuPuuid_ToRiotPuuid()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await InsertGame(scope, 8001, LcuPuuid);

        var updated = await scope.Initializer.ReconcileGamePuuidAsync(RiotPuuid);

        Assert.Equal(1, updated);
        Assert.Equal(RiotPuuid, await GetPuuid(scope, 8001));
    }

    [Fact]
    public async Task Reconcile_LeavesEmptyPuuidRowsAlone()
    {
        // Legacy '' rows already pass the read filter's "= ''" leg; claiming them
        // risks re-tagging genuinely-foreign imports. They must be left untouched.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await InsertGame(scope, 8002, "");

        var updated = await scope.Initializer.ReconcileGamePuuidAsync(RiotPuuid);

        Assert.Equal(0, updated);
        Assert.Equal("", await GetPuuid(scope, 8002));
    }

    [Fact]
    public async Task Reconcile_LeavesAlreadyCorrectRowsAlone()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await InsertGame(scope, 8003, RiotPuuid);

        var updated = await scope.Initializer.ReconcileGamePuuidAsync(RiotPuuid);

        Assert.Equal(0, updated);
        Assert.Equal(RiotPuuid, await GetPuuid(scope, 8003));
    }

    [Fact]
    public async Task Reconcile_LoggedOut_IsNoOp()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await InsertGame(scope, 8004, LcuPuuid);

        var updated = await scope.Initializer.ReconcileGamePuuidAsync("");

        Assert.Equal(0, updated);
        Assert.Equal(LcuPuuid, await GetPuuid(scope, 8004));
    }

    [Fact]
    public async Task Reconcile_IsIdempotent()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await InsertGame(scope, 8005, LcuPuuid);

        var first = await scope.Initializer.ReconcileGamePuuidAsync(RiotPuuid);
        var second = await scope.Initializer.ReconcileGamePuuidAsync(RiotPuuid);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // WHERE guard makes the re-run a no-op
        Assert.Equal(RiotPuuid, await GetPuuid(scope, 8005));
    }

    [Fact]
    public async Task Reconcile_ReClaimsAnyForeignButYoursValue()
    {
        // Scope is "any non-empty, non-matching value" — not just UUID-shaped LCU
        // ids — because a captured game is always the local (logged-in) player's.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await InsertGame(scope, 8006, ForeignPuuid);

        var updated = await scope.Initializer.ReconcileGamePuuidAsync(RiotPuuid);

        Assert.Equal(1, updated);
        Assert.Equal(RiotPuuid, await GetPuuid(scope, 8006));
    }

    [Fact]
    public async Task Reconcile_UnhidesStrandedGame_ForDashboardStats()
    {
        // End-to-end: a stranded LCU-puuid game is invisible to the dashboard's
        // account-scoped today-stats BEFORE reconcile, and visible AFTER. This is
        // the exact path that produced "GAMES 0 even though I played 3".
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        await InsertGame(scope, 8101, LcuPuuid, win: true, date: Today);
        await InsertGame(scope, 8102, LcuPuuid, win: false, date: Today);
        await scope.SessionLog.LogGameAsync(8101, "Ornn", win: true, mentalRating: 10);
        await scope.SessionLog.LogGameAsync(8102, "Sivir", win: false, mentalRating: 4);

        var before = await scope.SessionLog.GetStatsForDateAsync(Today, RiotPuuid);
        Assert.Equal(0, before.Games); // hidden by the puuid mismatch

        await scope.Initializer.ReconcileGamePuuidAsync(RiotPuuid);

        var after = await scope.SessionLog.GetStatsForDateAsync(Today, RiotPuuid);
        Assert.Equal(2, after.Games);
        Assert.Equal(1, after.Wins);
        Assert.Equal(1, after.Losses);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task InsertGame(
        TestDatabaseScope scope, long gameId, string puuid, bool win = true, string? date = null)
    {
        var game = TestGameStatsFactory.Create(gameId, win: win);
        game.Puuid = puuid;
        await scope.Games.SaveAsync(game);

        // Pin puuid (and an explicit date_played when the test asserts on a day)
        // directly so we exercise ReconcileGamePuuidAsync, not the save path.
        // (Save-path puuid handling is covered by GameServicePuuidReconciliationTests.)
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET puuid = @p, date_played = COALESCE(@d, date_played) WHERE game_id = @g";
        cmd.Parameters.AddWithValue("@p", puuid);
        cmd.Parameters.AddWithValue("@d", (object?)(date != null ? date + " 12:00" : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@g", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> GetPuuid(TestDatabaseScope scope, long gameId)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(puuid, '') FROM games WHERE game_id = @g";
        cmd.Parameters.AddWithValue("@g", gameId);
        var result = await cmd.ExecuteScalarAsync();
        return result as string ?? "";
    }
}

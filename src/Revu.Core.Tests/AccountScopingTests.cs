using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Tests;

/// <summary>
/// Schema-v9 LENIENT account-scoping contract (P3a). The aggregate queries take
/// an optional <c>currentPuuid</c>; when supplied they keep the player's own rows
/// PLUS all legacy rows (puuid '' / NULL) and exclude only genuinely-foreign
/// accounts. The CRITICAL invariant is that legacy rows (puuid='', like the
/// existing chapy+bye history) are NEVER hidden — not in the scoped path, not in
/// the no-op path. Null/empty currentPuuid is a NO-OP (all rows, pre-scoping
/// behavior).
/// </summary>
public sealed class AccountScopingTests
{
    private const string SelfPuuid = "PUUID-SELF-0001";
    private const string ForeignPuuid = "PUUID-FOREIGN-9999";

    private static GameStats Game(long id, string puuid, bool win = true) =>
        new()
        {
            GameId = id,
            Timestamp = id,
            GameDuration = 1800,
            GameMode = "Ranked Solo",
            GameType = "MATCHED_GAME",
            QueueType = "Ranked Solo/Duo",
            SummonerName = "Tester",
            ChampionName = "Ahri",
            ChampionId = 103,
            TeamId = 100,
            Position = "MIDDLE",
            Win = win,
            Kills = 8,
            Deaths = 3,
            Assists = 6,
            KdaRatio = 4.67,
            CsTotal = 210,
            CsPerMin = 7.0,
            VisionScore = 24,
            TeamKills = 20,
            KillParticipation = 70,
            Puuid = puuid,
        };

    // ── 5. games.puuid round-trips ───────────────────────────────────────────

    [Fact]
    public async Task SavedGameStats_Puuid_RoundTrips()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        await scope.Games.SaveAsync(Game(7001, SelfPuuid));

        var read = await scope.Games.GetAsync(7001);
        Assert.NotNull(read);
        Assert.Equal(SelfPuuid, read!.Puuid);

        // Legacy capture path leaves puuid empty — that must round-trip as ""
        // (not null), since the lenient scope keys off '' to keep legacy rows.
        await scope.Games.SaveAsync(Game(7002, ""));
        var legacy = await scope.Games.GetAsync(7002);
        Assert.NotNull(legacy);
        Assert.Equal("", legacy!.Puuid);
    }

    // ── (a) CRITICAL: legacy rows (puuid='') are ALWAYS kept ─────────────────

    [Fact]
    public async Task LegacyRows_AreAlwaysIncluded_RegardlessOfCurrentPuuid()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameAnalyticsQuery analytics = scope.Games;

        // Two legacy rows (puuid='', like today's chapy+bye history).
        await scope.Games.SaveAsync(Game(1001, "", win: true));
        await scope.Games.SaveAsync(Game(1002, "", win: false));

        // Scoping to a logged-in account that owns NONE of these rows must STILL
        // return both legacy rows. This is the no-hide-history guarantee.
        var scoped = await analytics.GetOverallStatsAsync(SelfPuuid);
        Assert.Equal(2, scoped.TotalGames);
        Assert.Equal(1, scoped.TotalWins);

        // Champion stats path keeps them too.
        var champ = await analytics.GetChampionStatsAsync(SelfPuuid);
        var ahri = Assert.Single(champ, c => c.ChampionName == "Ahri");
        Assert.Equal(2, ahri.GamesPlayed);
    }

    // ── (b) foreign non-empty accounts are EXCLUDED when scoped ──────────────

    [Fact]
    public async Task ForeignAccountRows_AreExcluded_WhenScopedToSelf()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameAnalyticsQuery analytics = scope.Games;

        await scope.Games.SaveAsync(Game(2001, SelfPuuid, win: true));     // mine
        await scope.Games.SaveAsync(Game(2002, "", win: true));            // legacy
        await scope.Games.SaveAsync(Game(2003, ForeignPuuid, win: false)); // not mine
        await scope.Games.SaveAsync(Game(2004, ForeignPuuid, win: false)); // not mine

        var scoped = await analytics.GetOverallStatsAsync(SelfPuuid);

        // Self (1) + legacy (1) = 2; the two foreign rows are dropped.
        Assert.Equal(2, scoped.TotalGames);
        Assert.Equal(2, scoped.TotalWins);
    }

    // ── (c) rows whose puuid EQUALS currentPuuid are included ────────────────

    [Fact]
    public async Task OwnRows_AreIncluded_WhenScopedToSelf()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameAnalyticsQuery analytics = scope.Games;

        await scope.Games.SaveAsync(Game(3001, SelfPuuid, win: true));
        await scope.Games.SaveAsync(Game(3002, SelfPuuid, win: false));
        await scope.Games.SaveAsync(Game(3003, ForeignPuuid, win: true));

        var scoped = await analytics.GetOverallStatsAsync(SelfPuuid);

        Assert.Equal(2, scoped.TotalGames);
        Assert.Equal(1, scoped.TotalWins);
    }

    // ── (d) null/empty currentPuuid = NO-OP (all rows) ───────────────────────

    [Fact]
    public async Task NullOrEmptyPuuid_IsNoOp_ReturnsAllRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameAnalyticsQuery analytics = scope.Games;

        await scope.Games.SaveAsync(Game(4001, SelfPuuid, win: true));
        await scope.Games.SaveAsync(Game(4002, "", win: true));
        await scope.Games.SaveAsync(Game(4003, ForeignPuuid, win: false));

        // null default
        var unscoped = await analytics.GetOverallStatsAsync();
        Assert.Equal(3, unscoped.TotalGames);
        Assert.Equal(2, unscoped.TotalWins);

        // explicit empty string is the same no-op
        var emptyScope = await analytics.GetOverallStatsAsync("");
        Assert.Equal(3, emptyScope.TotalGames);
        Assert.Equal(2, emptyScope.TotalWins);
    }

    // ── SessionLog aggregate honors the same lenient contract ────────────────

    [Fact]
    public async Task SessionLog_StatsForDate_KeepsLegacyAndOwn_ExcludesForeign()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // The session_log date is resolved from the game's date_played, which the
        // write path derives as the LOCAL calendar day of the timestamp. Compute
        // the expected date the same way so the assertion is timezone-robust.
        long ts = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        string date = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("yyyy-MM-dd");

        var mine = Game(5001, SelfPuuid, win: true); mine.Timestamp = ts;
        var legacy = Game(5002, "", win: false); legacy.Timestamp = ts;
        var foreign = Game(5003, ForeignPuuid, win: true); foreign.Timestamp = ts;
        await scope.Games.SaveAsync(mine);
        await scope.Games.SaveAsync(legacy);
        await scope.Games.SaveAsync(foreign);

        await scope.SessionLog.LogGameAsync(5001, "Ahri", win: true);
        await scope.SessionLog.LogGameAsync(5002, "Ahri", win: false);
        await scope.SessionLog.LogGameAsync(5003, "Ahri", win: true);

        // Scoped to self: own (1) + legacy (1), foreign dropped.
        var scoped = await scope.SessionLog.GetStatsForDateAsync(date, SelfPuuid);
        Assert.Equal(2, scoped.Games);
        Assert.Equal(1, scoped.Wins);

        // No-op: all three.
        var unscoped = await scope.SessionLog.GetStatsForDateAsync(date);
        Assert.Equal(3, unscoped.Games);
        Assert.Equal(2, unscoped.Wins);
    }

    [Fact]
    public async Task SessionLog_ManualRow_NoGamesJoin_IsAlwaysKept()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // A manual/unmatched session_log row has game_id = NULL (no games row),
        // so vg.puuid IS NULL via the LEFT JOIN — it must survive even when scoped
        // to a foreign account. LogGameAsync can't create a NULL-game_id row (the
        // games FK would fire), so insert one directly the way the manual-log path
        // does.
        const string date = "2026-06-19";
        await using (var conn = scope.OpenConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO session_log (date, game_id, champion_name, win, mental_rating, timestamp)
                VALUES (@date, NULL, 'Yasuo', 1, 5, @ts)";
            cmd.Parameters.AddWithValue("@date", date);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        // Scoped to a foreign account: the manual row (vg.puuid IS NULL) is kept.
        var scoped = await scope.SessionLog.GetStatsForDateAsync(date, ForeignPuuid);
        Assert.Equal(1, scoped.Games);
        Assert.Equal(1, scoped.Wins);
    }
}

using Microsoft.Data.Sqlite;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// P2b (digest 2026-06-12): per-rule behavioral records are computed from game
/// outcomes/times, never from session_log.rule_broken (clearing-censored).
/// A "trigger game" is a game played while the rule's condition already held.
/// </summary>
public sealed class RulesRepositoryEvidenceTests
{
    [Fact]
    public async Task GetRuleEvidence_CountsTriggerGamesBehaviorally_PerRuleType()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        // Day 1 (local), 40-minute spacing:
        //   #1 W  · #2 L (mental 3) · #3 L (mental 6) · #4 L (mental 2) · #5 W
        // Consecutive losses BEFORE each game: 0, 0, 1, 2, 3
        //   → loss_streak(2) triggers on #4 and #5 (1W–1L).
        // Same-day index BEFORE each game: 0..4 → max_games(3) triggers on #4, #5.
        // min_mental (P-015 semantics): the trip is a game played within the 2h
        // cool-off after a TILTED game (mental ≤ 3). Previous game's mental
        // BEFORE each: -, 3, 6, 2 (hidden game has none). Tilted-prior + <2h gap:
        //   #3 (prev #2 = 3, 40m) and #5 (prev #4 = 2, 40m) → triggers (1W–1L).
        //   #4's prior (#3 = 6) is not tilted, so #4 does not trip on min_mental.
        // A hidden loss between #2 and #3 must not shift any of those counts.
        // Day 2: one loss — day boundary resets every counter, so no triggers.
        var day1 = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(10));
        var day2 = day1.AddDays(1);
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 9001, win: true, day1, hidden: false);
            await InsertGameAsync(conn, 9002, win: false, day1.AddMinutes(40), hidden: false, mental: 3);
            await InsertGameAsync(conn, 9099, win: false, day1.AddMinutes(60), hidden: true);
            await InsertGameAsync(conn, 9003, win: false, day1.AddMinutes(80), hidden: false, mental: 6);
            await InsertGameAsync(conn, 9004, win: false, day1.AddMinutes(120), hidden: false, mental: 2);
            await InsertGameAsync(conn, 9005, win: true, day1.AddMinutes(160), hidden: false);
            await InsertGameAsync(conn, 9006, win: false, day2, hidden: false);
        }

        var lossStreakId = await rules.CreateAsync("Stop after 2 losses", ruleType: "loss_streak", conditionValue: "2:120");
        var maxGamesId = await rules.CreateAsync("Max 3 games", ruleType: "max_games", conditionValue: "3");
        var minMentalId = await rules.CreateAsync("Min mental 5", ruleType: "min_mental", conditionValue: "5");
        var customId = await rules.CreateAsync("Sleep well", ruleType: "custom");

        var evidence = await rules.GetRuleEvidenceAsync(await rules.GetAllAsync());

        var lossStreak = evidence[lossStreakId];
        Assert.Equal(2, lossStreak.TriggerGames);
        Assert.Equal(1, lossStreak.TriggerWins);
        Assert.Equal(6, lossStreak.BaselineGames);   // hidden game excluded
        Assert.Equal(2, lossStreak.BaselineWins);
        Assert.Equal(day1.ToString("yyyy-MM-dd"), lossStreak.LastTriggerDate);

        var maxGames = evidence[maxGamesId];
        Assert.Equal(2, maxGames.TriggerGames);
        Assert.Equal(1, maxGames.TriggerWins);

        var minMental = evidence[minMentalId];
        Assert.Equal(2, minMental.TriggerGames);
        Assert.Equal(1, minMental.TriggerWins);

        Assert.False(evidence.ContainsKey(customId)); // no automated record for custom rules
    }

    [Fact]
    public async Task GetBehavioralAdherenceStreak_MinMental_SubThresholdButNotTilted_DoesNotTrip()
    {
        // P-015 regression — the reported day, verbatim:
        //   16:11 Loss (mental 6) → 16:42 Win (m7) → 17:18 Win (m9)
        //   → [3h41m break] → 20:59 Win (m10)
        // The old reconstruction tripped min_mental at 16:42 (prior game m6 was
        // below the configured threshold of 7), zeroing a 51-day streak. Under
        // the simplified rule, m6 is NOT tilted (floor = 3), so nothing arms the
        // cool-off and today stays a clean streak day.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var dayMinus1 = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(10));
        var today = new DateTimeOffset(DateTime.Today.AddHours(16).AddMinutes(11));
        using (var conn = scope.OpenConnection())
        {
            await InsertRuleAsync(conn, "Min mental 7", "min_mental", "7",
                createdAt: dayMinus1.AddDays(-10).ToUnixTimeSeconds());

            // A prior clean day so the streak has something to count back to.
            await InsertGameAsync(conn, 9501, win: true, dayMinus1, hidden: false, mental: 8);

            // Today: the exact reported sequence.
            await InsertGameAsync(conn, 9502, win: false, today, hidden: false, mental: 6);
            await InsertGameAsync(conn, 9503, win: true, today.AddMinutes(31), hidden: false, mental: 7);
            await InsertGameAsync(conn, 9504, win: true, today.AddMinutes(67), hidden: false, mental: 9);
            await InsertGameAsync(conn, 9505, win: true, today.AddHours(3).AddMinutes(41 + 67), hidden: false, mental: 10);
        }

        // Today + yesterday are both clean → streak of 2, NOT 0.
        Assert.Equal(2, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
    }

    [Fact]
    public async Task GetBehavioralAdherenceStreak_MinMental_TiltedThenCoolOff_DoesNotTrip()
    {
        // The prescribed remedy works: a tilted game (≤3) followed by a 2h+
        // break is NOT a trip — the break is exactly what the rule asks for.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var today = new DateTimeOffset(DateTime.Today.AddHours(12));
        using (var conn = scope.OpenConnection())
        {
            await InsertRuleAsync(conn, "Min mental 7", "min_mental", "7",
                createdAt: today.AddDays(-10).ToUnixTimeSeconds());

            await InsertGameAsync(conn, 9601, win: false, today, hidden: false, mental: 2);
            // Next game is 2h1m later — outside the cool-off window.
            await InsertGameAsync(conn, 9602, win: true, today.AddHours(2).AddMinutes(1), hidden: false, mental: 8);
        }

        Assert.Equal(1, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
    }

    [Fact]
    public async Task GetBehavioralAdherenceStreak_MinMental_TiltedThenRequeue_Trips()
    {
        // The behavior the rule exists to catch: a tilted game (≤3) and the
        // player requeues inside the 2h cool-off instead of resting → trip.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var dayMinus1 = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(12));
        using (var conn = scope.OpenConnection())
        {
            await InsertRuleAsync(conn, "Min mental 7", "min_mental", "7",
                createdAt: dayMinus1.AddDays(-10).ToUnixTimeSeconds());

            // Day -1: tilted game, then requeue 30m later (inside cool-off) → trip.
            await InsertGameAsync(conn, 9701, win: false, dayMinus1, hidden: false, mental: 3);
            await InsertGameAsync(conn, 9702, win: false, dayMinus1.AddMinutes(30), hidden: false, mental: 4);
            // Today: clean single game.
            await InsertGameAsync(conn, 9703, win: true, dayMinus1.AddDays(1), hidden: false, mental: 8);
        }

        // Today is clean (streak 1); day -1 tripped, so the walk stops there.
        Assert.Equal(1, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));

        // And the per-rule record counts that trip.
        var evidence = await rules.GetRuleEvidenceAsync(await rules.GetAllAsync());
        var record = Assert.Single(evidence.Values);
        Assert.Equal(1, record.TriggerGames);
    }

    [Fact]
    public async Task GetBehavioralAdherenceStreak_CountsCleanPlayDays_StopsAtTriggerDay()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        // No rules yet → no streak to speak of.
        Assert.Equal(0, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));

        var dayMinus2 = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(10));
        using (var conn = scope.OpenConnection())
        {
            // Rule predates all games, so every day is in scope.
            await InsertRuleAsync(conn, "Stop after 2 losses", "loss_streak", "2",
                createdAt: dayMinus2.AddDays(-10).ToUnixTimeSeconds());

            // Day -2: W L L L → the 4th game is played at 2 consecutive
            // losses → trigger day. Day -1 and today: clean play-days.
            await InsertGameAsync(conn, 9101, win: true, dayMinus2, hidden: false);
            await InsertGameAsync(conn, 9102, win: false, dayMinus2.AddMinutes(40), hidden: false);
            await InsertGameAsync(conn, 9103, win: false, dayMinus2.AddMinutes(80), hidden: false);
            await InsertGameAsync(conn, 9104, win: false, dayMinus2.AddMinutes(120), hidden: false);
            await InsertGameAsync(conn, 9105, win: true, dayMinus2.AddDays(1), hidden: false);
            await InsertGameAsync(conn, 9106, win: false, dayMinus2.AddDays(2), hidden: false);
        }

        // Epoch pinned to the past so every day gets behavioral judgment.
        Assert.Equal(2, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
    }

    [Fact]
    public async Task GetBehavioralAdherenceStreak_SkippedTriggerGames_AreStreakNeutral()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var dayMinus2 = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(10));
        using (var conn = scope.OpenConnection())
        {
            await InsertRuleAsync(conn, "Stop after 2 losses", "loss_streak", "2",
                createdAt: dayMinus2.AddDays(-10).ToUnixTimeSeconds());

            // Same trigger pattern as the base test, but the trigger game is
            // marked skipped — the player's deliberate streak-protection
            // lever — so day -2 stays a clean streak day.
            await InsertGameAsync(conn, 9301, win: true, dayMinus2, hidden: false);
            await InsertGameAsync(conn, 9302, win: false, dayMinus2.AddMinutes(40), hidden: false);
            await InsertGameAsync(conn, 9303, win: false, dayMinus2.AddMinutes(80), hidden: false);
            await InsertGameAsync(conn, 9304, win: false, dayMinus2.AddMinutes(120), hidden: false, skipped: true);
            await InsertGameAsync(conn, 9305, win: true, dayMinus2.AddDays(1), hidden: false);
            await InsertGameAsync(conn, 9306, win: false, dayMinus2.AddDays(2), hidden: false);
        }

        Assert.Equal(3, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));

        // The per-rule record stays unforgiving: the skipped trip still counts.
        var evidence = await rules.GetRuleEvidenceAsync(await rules.GetAllAsync());
        var record = Assert.Single(evidence.Values);
        Assert.Equal(1, record.TriggerGames);
    }

    [Fact]
    public async Task GetBehavioralAdherenceStreak_RuleOnlyJudgesGamesAfterItsCreation()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var dayMinus2 = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(10));
        using (var conn = scope.OpenConnection())
        {
            // Same trigger pattern on day -2, but the rule is created TODAY:
            // pre-rule days are out of scope entirely, so only today counts.
            await InsertGameAsync(conn, 9201, win: false, dayMinus2, hidden: false);
            await InsertGameAsync(conn, 9202, win: false, dayMinus2.AddMinutes(40), hidden: false);
            await InsertGameAsync(conn, 9203, win: false, dayMinus2.AddMinutes(80), hidden: false);
            await InsertGameAsync(conn, 9204, win: true, dayMinus2.AddDays(1), hidden: false);
            await InsertGameAsync(conn, 9205, win: true, dayMinus2.AddDays(2), hidden: false);

            await InsertRuleAsync(conn, "Stop after 2 losses", "loss_streak", "2",
                createdAt: new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds());
        }

        Assert.Equal(1, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
    }

    [Fact]
    public async Task GetBehavioralAdherenceStreak_PreEpochDays_KeepFlagEraVerdicts()
    {
        // The re-base must not retroactively erase an earned streak: days
        // before the behavioral epoch are judged by surviving non-skipped
        // rule_broken flags (the old standard), days from the epoch onward
        // behaviorally. Same data, two judges, two answers — by design.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var dayMinus3 = new DateTimeOffset(DateTime.Today.AddDays(-3).AddHours(10));
        using (var conn = scope.OpenConnection())
        {
            await InsertRuleAsync(conn, "Stop after 2 losses", "loss_streak", "2",
                createdAt: dayMinus3.AddDays(-10).ToUnixTimeSeconds());

            // Day -3: W L L L — a behavioral trigger day, but NO flag stamped
            // (or it was cleared): clean under the flag era.
            await InsertGameAsync(conn, 9401, win: true, dayMinus3, hidden: false);
            await InsertGameAsync(conn, 9402, win: false, dayMinus3.AddMinutes(40), hidden: false);
            await InsertGameAsync(conn, 9403, win: false, dayMinus3.AddMinutes(80), hidden: false);
            await InsertGameAsync(conn, 9404, win: false, dayMinus3.AddMinutes(120), hidden: false);
            // Day -2: behaviorally clean single game, but a surviving flag:
            // a trip day under the flag era.
            await InsertGameAsync(conn, 9405, win: true, dayMinus3.AddDays(1), hidden: false, ruleBroken: true);
            // Day -1 and today: clean.
            await InsertGameAsync(conn, 9406, win: true, dayMinus3.AddDays(2), hidden: false);
            await InsertGameAsync(conn, 9407, win: false, dayMinus3.AddDays(3), hidden: false);
        }

        // Epoch in the future → every day is pre-epoch (flag-judged):
        // today + day -1 are clean, the surviving flag on day -2 breaks it.
        Assert.Equal(2, await rules.GetBehavioralAdherenceStreakAsync("2099-01-01"));

        // Epoch in the past → every day is behavioral: the flag on day -2 is
        // ignored (clearing-immune in both directions), the loss-streak
        // pattern on day -3 breaks it instead.
        Assert.Equal(3, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
    }

    private static async Task InsertRuleAsync(
        SqliteConnection conn, string name, string ruleType, string conditionValue, long createdAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO rules (name, rule_type, condition_value, is_active, created_at)
            VALUES (@name, @ruleType, @conditionValue, 1, @createdAt)";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@ruleType", ruleType);
        cmd.Parameters.AddWithValue("@conditionValue", conditionValue);
        cmd.Parameters.AddWithValue("@createdAt", createdAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertGameAsync(
        SqliteConnection conn, long gameId, bool win, DateTimeOffset at, bool hidden,
        int? mental = null, bool skipped = false, bool ruleBroken = false)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO games (game_id, champion_name, win, timestamp, queue_type, is_hidden)
                VALUES (@gameId, 'Ahri', @win, @timestamp, 'Ranked Solo/Duo', @hidden)";
            cmd.Parameters.AddWithValue("@gameId", gameId);
            cmd.Parameters.AddWithValue("@win", win ? 1 : 0);
            cmd.Parameters.AddWithValue("@timestamp", at.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@hidden", hidden ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        if (mental is not null || skipped || ruleBroken)
        {
            using var slCmd = conn.CreateCommand();
            slCmd.CommandText = @"
                INSERT INTO session_log (date, game_id, champion_name, win, mental_rating, is_skipped, rule_broken, timestamp)
                VALUES (@date, @gameId, 'Ahri', @win, @mental, @skipped, @ruleBroken, @timestamp)";
            slCmd.Parameters.AddWithValue("@date", at.ToString("yyyy-MM-dd"));
            slCmd.Parameters.AddWithValue("@gameId", gameId);
            slCmd.Parameters.AddWithValue("@win", win ? 1 : 0);
            slCmd.Parameters.AddWithValue("@mental", mental is int rating ? rating : DBNull.Value);
            slCmd.Parameters.AddWithValue("@skipped", skipped ? 1 : 0);
            slCmd.Parameters.AddWithValue("@ruleBroken", ruleBroken ? 1 : 0);
            slCmd.Parameters.AddWithValue("@timestamp", at.ToUnixTimeSeconds());
            await slCmd.ExecuteNonQueryAsync();
        }
    }
}

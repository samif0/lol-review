using Microsoft.Data.Sqlite;

namespace Revu.Core.Tests;

/// <summary>
/// v3.3 (schema v12) coaching stints: the stint lifecycle contract (one active
/// stint, ending keeps the rows), the 1-based within-stint block numbering
/// (sticky across same-day re-locks — the sequence never shifts or
/// double-counts), and the with-coach/solo block counters the feature exists
/// to gather.
/// </summary>
public sealed class CoachingStintsRepositoryTests
{
    [Fact]
    public async Task StartStint_GetActive_EndStint_Lifecycle()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        Assert.Null(await scope.CoachingStints.GetActiveStintAsync());

        var id = await scope.CoachingStints.StartStintAsync("Violet", "2026-07-30", "2026-12-30");
        Assert.True(id > 0);

        var active = await scope.CoachingStints.GetActiveStintAsync();
        Assert.NotNull(active);
        Assert.Equal(id, active!.Id);
        Assert.Equal("Violet", active.Name);
        Assert.Equal("2026-07-30", active.StartDate);
        Assert.Equal("2026-12-30", active.PlannedEndDate);
        Assert.Null(active.EndedAt);

        await scope.CoachingStints.EndStintAsync(id);
        Assert.Null(await scope.CoachingStints.GetActiveStintAsync());

        // Ending is not a delete — the row survives with ended_at stamped, and
        // a later stint starts cleanly as the new active one.
        var second = await scope.CoachingStints.StartStintAsync("Violet II", "2027-01-15");
        var newActive = await scope.CoachingStints.GetActiveStintAsync();
        Assert.NotNull(newActive);
        Assert.Equal(second, newActive!.Id);
        Assert.Equal("", newActive.PlannedEndDate);
    }

    [Fact]
    public async Task BlockNumbering_CountsWithinStint_AndSurvivesRelock()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var stintId = await scope.CoachingStints.StartStintAsync("Violet", "2026-07-30");

        // First block of the stint gets #1.
        Assert.Equal(1, await scope.CoachingStints.GetNextBlockNumberAsync(stintId));
        await scope.SessionLog.SetSessionIntentionAsync(
            "2026-07-30", "wave management", withCoach: true, stintId: stintId, stintBlockNumber: 1);

        // Next day: #2 (the day-1 stamp raised the MAX).
        Assert.Equal(2, await scope.CoachingStints.GetNextBlockNumberAsync(stintId));
        await scope.SessionLog.SetSessionIntentionAsync(
            "2026-07-31", "jungle tracking", withCoach: false, stintId: stintId, stintBlockNumber: 2);

        // Same-day RE-LOCK: the upsert must retag with_coach but keep the
        // original number (sticky), even when the caller re-computes "next".
        var next = await scope.CoachingStints.GetNextBlockNumberAsync(stintId);
        Assert.Equal(3, next);
        await scope.SessionLog.SetSessionIntentionAsync(
            "2026-07-31", "jungle tracking, again", withCoach: true, stintId: stintId, stintBlockNumber: next);

        var relocked = await scope.SessionLog.GetSessionAsync("2026-07-31");
        Assert.NotNull(relocked);
        Assert.Equal(2, relocked!.StintBlockNumber);           // number kept
        Assert.True(relocked.WithCoach);                        // tag updated
        Assert.Equal("jungle tracking, again", relocked.Intention);
        Assert.Equal(stintId, relocked.StintId);

        // The sticky re-lock did NOT burn number 3 — the true next is still 3.
        Assert.Equal(3, await scope.CoachingStints.GetNextBlockNumberAsync(stintId));
    }

    [Fact]
    public async Task BlockCounts_SplitByCoachTag()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var stintId = await scope.CoachingStints.StartStintAsync("Violet", "2026-07-30");
        await scope.SessionLog.SetSessionIntentionAsync("2026-07-30", "a", withCoach: true, stintId: stintId, stintBlockNumber: 1);
        await scope.SessionLog.SetSessionIntentionAsync("2026-07-31", "b", withCoach: false, stintId: stintId, stintBlockNumber: 2);
        await scope.SessionLog.SetSessionIntentionAsync("2026-08-01", "c", withCoach: false, stintId: stintId, stintBlockNumber: 3);
        // A standalone block outside any stint must not count.
        await scope.SessionLog.SetSessionIntentionAsync("2026-08-02", "solo day");

        var counts = await scope.CoachingStints.GetBlockCountsAsync(stintId);
        Assert.Equal(3, counts.Total);
        Assert.Equal(1, counts.WithCoach);
        Assert.Equal(2, counts.Solo);
    }

    [Fact]
    public async Task StandaloneBlock_KeepsNullStintColumns()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // No stint running — the plain Start Block path passes only defaults.
        await scope.SessionLog.SetSessionIntentionAsync("2026-07-30", "just play");

        var info = await scope.SessionLog.GetSessionAsync("2026-07-30");
        Assert.NotNull(info);
        Assert.Null(info!.StintId);
        Assert.Null(info.StintBlockNumber);
        Assert.False(info.WithCoach);

        // Verify the columns really are NULL (unstamped). NULL means the
        // FIRST stint-era re-lock that day claims the row (see the claiming
        // test below) — stickiness only stops an already-stamped row from
        // being renumbered within its stint.
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT stint_id, stint_block_number FROM sessions WHERE date = '2026-07-30'";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task PreStintBlock_IsClaimedByLaterSameDayRelock()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Morning solo block, no stint yet.
        await scope.SessionLog.SetSessionIntentionAsync("2026-07-30", "just play");

        // Midday: stint starts; evening re-lock runs under it and claims the
        // day-row as block #1 (one row per date — a recorded decision).
        var stintId = await scope.CoachingStints.StartStintAsync("Violet", "2026-07-30");
        var next = await scope.CoachingStints.GetNextBlockNumberAsync(stintId);
        Assert.Equal(1, next);
        await scope.SessionLog.SetSessionIntentionAsync(
            "2026-07-30", "evening focus", withCoach: false, stintId: stintId, stintBlockNumber: next);

        var info = await scope.SessionLog.GetSessionAsync("2026-07-30");
        Assert.Equal(stintId, info!.StintId);
        Assert.Equal(1, info.StintBlockNumber);
    }

    [Fact]
    public async Task SameDayStintSwitch_RestampsToNewStint()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Block under stint A…
        var stintA = await scope.CoachingStints.StartStintAsync("Violet", "2026-07-01");
        await scope.SessionLog.SetSessionIntentionAsync(
            "2026-07-30", "block under A", withCoach: true, stintId: stintA, stintBlockNumber: 5);

        // …then A ends and B starts the SAME day; the re-lock runs under B and
        // must restamp (stickiness is within-stint only, never cross-stint).
        await scope.CoachingStints.EndStintAsync(stintA);
        var stintB = await scope.CoachingStints.StartStintAsync("Violet II", "2026-07-30");
        var next = await scope.CoachingStints.GetNextBlockNumberAsync(stintB);
        Assert.Equal(1, next);
        await scope.SessionLog.SetSessionIntentionAsync(
            "2026-07-30", "block under B", withCoach: false, stintId: stintB, stintBlockNumber: next);

        var info = await scope.SessionLog.GetSessionAsync("2026-07-30");
        Assert.Equal(stintB, info!.StintId);
        Assert.Equal(1, info.StintBlockNumber);

        var countsA = await scope.CoachingStints.GetBlockCountsAsync(stintA);
        var countsB = await scope.CoachingStints.GetBlockCountsAsync(stintB);
        Assert.Equal(0, countsA.Total);
        Assert.Equal(1, countsB.Total);
    }

    [Fact]
    public async Task RelockAfterStintEnded_KeepsTheEraTheBlockStartedIn()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var stintId = await scope.CoachingStints.StartStintAsync("Violet", "2026-07-30");
        await scope.SessionLog.SetSessionIntentionAsync(
            "2026-07-30", "stint block", withCoach: true, stintId: stintId, stintBlockNumber: 3);
        await scope.CoachingStints.EndStintAsync(stintId);

        // Re-lock with no stint active (nulls): the stamp must survive.
        await scope.SessionLog.SetSessionIntentionAsync("2026-07-30", "post-stint reword", withCoach: true);

        var info = await scope.SessionLog.GetSessionAsync("2026-07-30");
        Assert.Equal(stintId, info!.StintId);
        Assert.Equal(3, info.StintBlockNumber);
    }

    [Fact]
    public async Task SecondActiveStint_IsBlockedByTheUniqueIndex()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        await scope.CoachingStints.StartStintAsync("Violet", "2026-07-30");

        // The route pre-checks for a friendly 400, but the partial unique
        // index is the race-proof backstop: a second active INSERT must fail
        // with a constraint violation (SQLITE_CONSTRAINT = 19), never create
        // a zombie second active stint.
        var ex = await Assert.ThrowsAsync<SqliteException>(
            () => scope.CoachingStints.StartStintAsync("Sneaky Double", "2026-07-30"));
        Assert.Equal(19, ex.SqliteErrorCode);

        var active = await scope.CoachingStints.GetActiveStintAsync();
        Assert.Equal("Violet", active!.Name);
    }
}

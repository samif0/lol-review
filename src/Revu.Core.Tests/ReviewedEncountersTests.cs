using Microsoft.Data.Sqlite;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class ReviewedEncountersTests
{
    [Theory]
    [InlineData("short", "TRADE", "SHORT_TRADE")]
    [InlineData("extended", "TRADE", "EXTENDED_TRADE")]
    [InlineData("all_in", "ALL_IN", "ALL_IN")]
    public async Task ReviewedClassification_IsIndependentOfHpLossAndDuration(string classification, string type, string token)
    {
        using var db = new Database();
        var repo = new ReviewedEncountersRepository(db);
        var id = await repo.SaveAsync(1, null, Guid.NewGuid().ToString(), 191, 191, classification, "Observed champion exchange");
        var e = Assert.Single(await new GameEventsRepository(db).GetEventsAsync(1));
        Assert.Equal(id, e.Id);
        Assert.Equal(type, e.EventType);
        var resolver = ObjectiveEventTieResolver.FromTies([(token, 10L, "Review")]);
        Assert.Single(resolver.ResolveForGame([e])[id]);
        Assert.DoesNotContain("hp_lost_pct", e.Details);
    }

    [Fact]
    public async Task AllIn_DoesNotMatchEitherTradeObjective_AndUncertainMatchesNeither()
    {
        using var db = new Database();
        var reviews = new ReviewedEncountersRepository(db);
        var id = await reviews.SaveAsync(1, null, Guid.NewGuid().ToString(), 100, 110, "all_in", null);
        var resolver = ObjectiveEventTieResolver.FromTies([
            ("TRADE", 1L, "Trades"), ("EXTENDED_TRADE", 2L, "Extended"), ("ALL_IN", 3L, "All-ins")]);
        var events = new GameEventsRepository(db);
        var e = Assert.Single(await events.GetEventsAsync(1));
        Assert.Equal(3L, Assert.Single(resolver.ResolveForGame([e])[id]).ObjectiveId);
        await reviews.SaveAsync(1, id, Guid.NewGuid().ToString(), 100, 110, "uncertain", null);
        Assert.Empty(resolver.ResolveForGame(await events.GetEventsAsync(1))[id]);
        Assert.Contains(GameEvent.TrackableTokens.Catalog, o => o.Token == "ALL_IN");
    }

    [Fact]
    public async Task CorrectionSurvivesCaptureReplacement_WithStableIdAndKillOutcome()
    {
        using var db = new Database();
        var events = new GameEventsRepository(db);
        var captured = new[] {
            new GameEvent { EventType = "TRADE", GameTimeS = 110, Details = "{\"kind\":\"extended\"}" },
            new GameEvent { EventType = "KILL", GameTimeS = 115 }
        };
        await events.SaveEventsAsync(1, captured);
        var original = (await events.GetEventsAsync(1))[0];
        var reviews = new ReviewedEncountersRepository(db);
        await reviews.SaveAsync(1, original.Id, Guid.NewGuid().ToString(), 100, 114, "all_in", "Committed pursuit");
        await events.SaveEventsAsync(1, captured);
        await events.SaveEventsAsync(1, await events.GetEventsAsync(1));
        var saved = await events.GetEventsAsync(1);
        Assert.Equal(2, saved.Count);
        var corrected = Assert.Single(saved, e => e.EventType == "ALL_IN");
        Assert.Equal(original.Id, corrected.Id);
        Assert.Equal(100, corrected.GameTimeS);
        Assert.Contains("Committed pursuit", corrected.Details);
        Assert.Single(saved, e => e.EventType == "KILL");
        await events.DeleteEventsByTypeAsync(1, "ALL_IN");
        Assert.Single(await events.GetEventsAsync(1), e => e.EventType == "ALL_IN");
    }

    [Fact]
    public async Task AddRetryIsIdempotent_AndPreservesSubsequentCorrection()
    {
        using var db = new Database();
        var repo = new ReviewedEncountersRepository(db);
        var request = Guid.NewGuid().ToString();
        var id = await repo.SaveAsync(1, null, request, 191, 192, "short", "Q bounce");
        await repo.SaveAsync(1, id, Guid.NewGuid().ToString(), 191, 194, "extended", "Corrected");
        Assert.Equal(id, await repo.SaveAsync(1, null, request, 191, 192, "short", "Q bounce"));
        var saved = Assert.Single(await new GameEventsRepository(db).GetEventsAsync(1));
        Assert.Contains("Corrected", saved.Details);
        Assert.Contains("extended", saved.Details);
    }

    [Fact]
    public async Task EscalationCanBeRepresentedAsSeparateSegments()
    {
        using var db = new Database();
        var repo = new ReviewedEncountersRepository(db);
        await repo.SaveAsync(1, null, Guid.NewGuid().ToString(), 100, 102, "short", null);
        await repo.SaveAsync(1, null, Guid.NewGuid().ToString(), 103, 105, "all_in", null);
        var saved = await new GameEventsRepository(db).GetEventsAsync(1);
        Assert.Equal(new[] { "TRADE", "ALL_IN" }, saved.Select(e => e.EventType));
    }

    [Theory]
    [InlineData(-1, 10, "short")]
    [InlineData(20, 10, "short")]
    [InlineData(100, 1001, "short")]
    [InlineData(10, 20, "guessed")]
    public async Task InvalidReviewDoesNotWrite(int start, int end, string classification)
    {
        using var db = new Database();
        await Assert.ThrowsAsync<ArgumentException>(() => new ReviewedEncountersRepository(db)
            .SaveAsync(1, null, Guid.NewGuid().ToString(), start, end, classification, null));
        Assert.Empty(await new GameEventsRepository(db).GetEventsAsync(1));
    }

    [Fact]
    public async Task CannotReclassifyKillOrAnotherGamesEvent()
    {
        using var db = new Database();
        var events = new GameEventsRepository(db);
        await events.SaveEventsAsync(1, [new() { EventType = "KILL", GameTimeS = 100 }]);
        var id = Assert.Single(await events.GetEventsAsync(1)).Id;
        var reviews = new ReviewedEncountersRepository(db);
        await Assert.ThrowsAsync<ArgumentException>(() => reviews.SaveAsync(1, id, Guid.NewGuid().ToString(), 100, 101, "short", null));
        await Assert.ThrowsAsync<ArgumentException>(() => reviews.SaveAsync(2, id, Guid.NewGuid().ToString(), 100, 101, "short", null));
        Assert.Equal("KILL", Assert.Single(await events.GetEventsAsync(1)).EventType);
    }

    private sealed class Database : IDbConnectionFactory, IDisposable
    {
        public string DatabasePath { get; } = $"encounters-{Guid.NewGuid():N}";
        private readonly SqliteConnection keeper;
        public Database()
        {
            keeper = CreateConnection();
            using var cmd = keeper.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE games(game_id INTEGER PRIMARY KEY, game_duration INTEGER);
                INSERT INTO games VALUES(1,1000),(2,1000);
                CREATE TABLE game_events(id INTEGER PRIMARY KEY AUTOINCREMENT, game_id INTEGER,
                    event_type TEXT, game_time_s INTEGER, details TEXT);
                """;
            cmd.ExecuteNonQuery();
            // v16: the encounter writer delegates to the corrections ledger, which needs
            // game_events.event_key and the event_corrections table (open decision 7).
            foreach (var statement in Schema.MigrateEventCorrections)
            {
                using var migrate = keeper.CreateCommand();
                migrate.CommandText = statement;
                migrate.ExecuteNonQuery();
            }
        }
        public SqliteConnection CreateConnection()
        {
            var conn = new SqliteConnection($"Data Source={DatabasePath};Mode=Memory;Cache=Shared");
            conn.Open();
            return conn;
        }
        public void Dispose() => keeper.Dispose();
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// v3.10.1: the matchup (lane, lane opponent, role→champion map) must be on the
/// game row the moment the game ends — not after a Match-V5 round-trip, not
/// after a manual Settings backfill. The LCU end-of-game payload has carried no
/// per-player position since 2026-08-14, so the capture resolves it from the
/// live roster, then champ select, then role priors, and stamps how sure it is
/// (<see cref="MatchupSources"/>) so Match-V5 still confirms an estimate.
/// </summary>
public sealed class MatchupAtGameEndTests
{
    private const string Own = "own";
    private const string Enemy = "enemy";

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <summary>A ranked EOG payload as the client emits it today: every position blank.</summary>
    private static JsonElement Eog(
        (string Champ, string Pos)[] ownTeam,
        (string Champ, string Pos)[] enemyTeam,
        string me,
        int myTeam = 100,
        string gameMode = "CLASSIC",
        string queueType = "RANKED_SOLO_5x5",
        string mePos = "")
    {
        static string Players((string Champ, string Pos)[] team) => string.Join(",", team.Select(p =>
            $$$"""{ "championName": "{{{p.Champ}}}", "selectedPosition": "{{{p.Pos}}}", "detectedTeamPosition": "{{{p.Pos}}}", "stats": { "CHAMPIONS_KILLED": 1 } }"""));
        var enemyTeamId = myTeam == 100 ? 200 : 100;
        var json = $$$"""
            {
              "gameId": 5000000102,
              "gameLength": 1104,
              "gameMode": "{{{gameMode}}}",
              "queueType": "{{{queueType}}}",
              "gameType": "MATCHED_GAME",
              "localPlayer": {
                "teamId": {{{myTeam}}}, "championName": "{{{me}}}", "championId": 21,
                "selectedPosition": "{{{mePos}}}", "detectedTeamPosition": "{{{mePos}}}",
                "stats": { "CHAMPIONS_KILLED": "7", "NUM_DEATHS": "2", "ASSISTS": "9", "WIN": "1" }
              },
              "teams": [
                { "teamId": {{{myTeam}}}, "stats": { "CHAMPIONS_KILLED": 20 }, "players": [ {{{Players(ownTeam)}}} ] },
                { "teamId": {{{enemyTeamId}}}, "stats": { "CHAMPIONS_KILLED": 12 }, "players": [ {{{Players(enemyTeam)}}} ] }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static (string, string)[] Blank(params string[] champs) => champs.Select(c => (c, "")).ToArray();

    // A full ranked lobby: Miss Fortune vs Yasuo bot.
    private static readonly string[] OwnComp = ["Teemo", "Zaahen", "Riven", "Miss Fortune", "Pantheon"];
    private static readonly string[] EnemyComp = ["Gragas", "Hecarim", "Swain", "Yasuo", "Soraka"];

    /// <summary>The live client's /playerlist for that lobby: the lanes the matchmaker assigned.</summary>
    private static JsonElement PlayerList(bool withPositions = true)
    {
        var lanes = new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" };
        var rows = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var pos = withPositions ? lanes[i] : "";
            rows.Add($$$"""{ "championName": "{{{OwnComp[i]}}}", "position": "{{{pos}}}", "team": "ORDER", "riotIdGameName": "own{{{i}}}", "summonerName": "own{{{i}}}", "isBot": false }""");
            rows.Add($$$"""{ "championName": "{{{EnemyComp[i]}}}", "position": "{{{pos}}}", "team": "CHAOS", "riotIdGameName": "enemy{{{i}}}", "summonerName": "enemy{{{i}}}", "isBot": false }""");
        }
        using var doc = JsonDocument.Parse("[" + string.Join(",", rows) + "]");
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string> Map(GameStats g) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(g.ParticipantMap)!;

    // ── LiveRoster ───────────────────────────────────────────────────────────

    [Fact]
    public void LiveRoster_Parse_ReadsChampionsTeamsAndLanes()
    {
        var roster = LiveRoster.Parse(PlayerList())!;

        Assert.Equal(10, roster.Players.Count);
        Assert.True(roster.IsComplete);
        Assert.True(roster.HasPositions);
        Assert.Equal("BOTTOM", roster.PositionOf("Miss Fortune", 100));
        Assert.Equal("BOTTOM", roster.PositionOf("MissFortune", 100));   // Match-V5 spelling
        Assert.Equal("BOTTOM", roster.PositionOf("Yasuo", 200));
        Assert.Equal("", roster.PositionOf("Yasuo", 100));                // wrong team → no answer
        Assert.Equal("BOTTOM", roster.PositionOf("Yasuo", 0));            // either team
        Assert.Equal("", roster.PositionOf("Ahri", 0));                   // not in the game
    }

    [Fact]
    public void LiveRoster_Parse_AramHasNoLanes_AndPlaceholdersReadAsBlank()
    {
        var roster = LiveRoster.Parse(PlayerList(withPositions: false))!;
        Assert.True(roster.IsComplete);
        Assert.False(roster.HasPositions);

        using var doc = JsonDocument.Parse("""[{ "championName": "Ahri", "position": "NONE", "team": "ORDER" }, { "championName": "", "position": "TOP", "team": "CHAOS" }]""");
        var odd = LiveRoster.Parse(doc.RootElement.Clone())!;
        Assert.Single(odd.Players);            // the nameless row is dropped
        Assert.Equal("", odd.Players[0].Position);
        Assert.False(odd.IsComplete);
    }

    [Fact]
    public void LiveRoster_Parse_RejectsNonArray()
    {
        using var doc = JsonDocument.Parse("""{ "errorCode": "RESOURCE_NOT_FOUND" }""");
        Assert.Null(LiveRoster.Parse(doc.RootElement.Clone()));
    }

    // ── StatsExtractor + live roster ─────────────────────────────────────────

    /// <summary>THE case: the payload has no positions, the live roster has them
    /// all → the row leaves the capture with lane, opponent and the full map.</summary>
    [Fact]
    public void ExtractFromEog_FillsTheWholeMatchupFromTheLiveRoster_WhenThePayloadHasNoPositions()
    {
        var eog = Eog(Blank(OwnComp), Blank(EnemyComp), me: "Miss Fortune");
        var roster = LiveRoster.Parse(PlayerList());

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance, roster)!;

        Assert.Equal("BOTTOM", stats.Position);
        Assert.Equal("Yasuo", stats.EnemyLaner);
        Assert.Equal(MatchupSources.Live, stats.MatchupSource);
        var map = Map(stats);
        Assert.Equal(10, map.Count);
        Assert.Equal("Teemo", map["ownTop"]);
        Assert.Equal("Zaahen", map["ownJg"]);
        Assert.Equal("Riven", map["ownMid"]);
        Assert.Equal("Miss Fortune", map["ownBot"]);
        Assert.Equal("Pantheon", map["ownSupp"]);
        Assert.Equal("Gragas", map["enemyTop"]);
        Assert.Equal("Hecarim", map["enemyJg"]);
        Assert.Equal("Swain", map["enemyMid"]);
        Assert.Equal("Yasuo", map["enemyBot"]);
        Assert.Equal("Soraka", map["enemySupp"]);

        // What the Review hero and the Matchups journal render from the row alone.
        Assert.Equal("Miss Fortune+Pantheon vs Yasuo+Soraka",
            MatchupDisplay.Build(stats.ChampionName, stats.EnemyLaner, stats.Position, stats.ParticipantMap));
        var prefill = MatchupPrefill.FromGame(stats);
        Assert.NotNull(prefill);
        Assert.True(prefill!.CanCreateOutright);
        Assert.Equal("bot", prefill.Lane);
    }

    /// <summary>Still no guessing: without a roster and without positions the row
    /// leaves the capture blank (MatchupFallback is the sidecar's job).</summary>
    [Fact]
    public void ExtractFromEog_WithoutRosterOrPositions_LeavesTheMatchupBlank_AndUnstamped()
    {
        var eog = Eog(Blank(OwnComp), Blank(EnemyComp), me: "Miss Fortune");

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance)!;

        Assert.Equal("", stats.Position);
        Assert.Equal("", stats.EnemyLaner);
        Assert.Equal("", stats.ParticipantMap);
        Assert.Equal("", stats.MatchupSource);
        // The fallback's inputs are on the row.
        Assert.Equal(OwnComp, Assert.IsType<List<string>>(stats.RawStats["_own_champions"]));
        Assert.Equal(EnemyComp, Assert.IsType<List<string>>(stats.RawStats["_enemy_champions"]));
        Assert.Equal("CLASSIC", stats.RawStats["_game_mode_raw"]);
    }

    [Fact]
    public void ExtractFromEog_PayloadPositionsWin_AndStampEog()
    {
        var own = new[] { ("Teemo", "TOP"), ("Zaahen", "JUNGLE"), ("Riven", "MIDDLE"), ("Miss Fortune", "BOTTOM"), ("Pantheon", "UTILITY") };
        var enemy = new[] { ("Gragas", "TOP"), ("Hecarim", "JUNGLE"), ("Swain", "MIDDLE"), ("Yasuo", "BOTTOM"), ("Soraka", "UTILITY") };
        var eog = Eog(own, enemy, me: "Miss Fortune");
        // localPlayer's own position is blank in the fixture; the roster answers that one.
        var roster = LiveRoster.Parse(PlayerList());

        var withRoster = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance, roster)!;
        Assert.Equal("BOTTOM", withRoster.Position);
        Assert.Equal("Yasuo", withRoster.EnemyLaner);
        Assert.Equal(10, Map(withRoster).Count);
        Assert.Equal(MatchupSources.Live, withRoster.MatchupSource); // the roster contributed a lane

        var payloadOnly = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance)!;
        Assert.Equal(MatchupSources.Eog, payloadOnly.MatchupSource);
        Assert.Equal(10, Map(payloadOnly).Count);
    }

    /// <summary>ARAM: the roster has no lanes, so nothing is assigned and nothing is stamped.</summary>
    [Fact]
    public void ExtractFromEog_LaneLessRoster_AssignsNothing()
    {
        var eog = Eog(Blank(OwnComp), Blank(EnemyComp), me: "Miss Fortune", gameMode: "ARAM", queueType: "ARAM_UNRANKED_5x5");
        var roster = LiveRoster.Parse(PlayerList(withPositions: false));

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance, roster)!;

        Assert.Equal("", stats.Position);
        Assert.Equal("", stats.ParticipantMap);
        Assert.Equal("", stats.MatchupSource);
    }

    /// <summary>Blind pick can field the same champion on both teams; the roster is read per team.</summary>
    [Fact]
    public void ExtractFromEog_RosterLookupIsTeamAware()
    {
        var eog = Eog(Blank("Teemo", "Zaahen", "Riven", "Yasuo", "Pantheon"), Blank("Gragas", "Hecarim", "Yasuo", "Jinx", "Soraka"), me: "Yasuo");
        using var doc = JsonDocument.Parse("""
            [
              { "championName": "Yasuo", "position": "BOTTOM", "team": "ORDER" },
              { "championName": "Yasuo", "position": "MIDDLE", "team": "CHAOS" },
              { "championName": "Jinx",  "position": "BOTTOM", "team": "CHAOS" },
              { "championName": "Riven", "position": "MIDDLE", "team": "ORDER" }
            ]
            """);
        var roster = LiveRoster.Parse(doc.RootElement.Clone());

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance, roster)!;

        Assert.Equal("BOTTOM", stats.Position);   // our Yasuo, not theirs
        Assert.Equal("Jinx", stats.EnemyLaner);
        var map = Map(stats);
        Assert.Equal("Yasuo", map["ownBot"]);
        Assert.Equal("Yasuo", map["enemyMid"]);
        Assert.Equal("Riven", map["ownMid"]);
    }

    // ── MatchupFallback: champ select ────────────────────────────────────────

    /// <summary>When the payload and the live roster disagree (a lane swap the
    /// client saw), the payload's own assignment wins and the row says so.</summary>
    [Fact]
    public void ExtractFromEog_PayloadPositionsBeatADisagreeingRoster()
    {
        var own = new[] { ("Teemo", "TOP"), ("Zaahen", "JUNGLE"), ("Riven", "MIDDLE"), ("Miss Fortune", "BOTTOM"), ("Pantheon", "UTILITY") };
        var enemy = new[] { ("Gragas", "TOP"), ("Hecarim", "JUNGLE"), ("Swain", "MIDDLE"), ("Yasuo", "BOTTOM"), ("Soraka", "UTILITY") };
        var eog = Eog(own, enemy, me: "Miss Fortune", mePos: "BOTTOM");
        using var doc = JsonDocument.Parse("""
            [
              { "championName": "Miss Fortune", "position": "MIDDLE", "team": "ORDER" },
              { "championName": "Yasuo", "position": "MIDDLE", "team": "CHAOS" },
              { "championName": "Swain", "position": "BOTTOM", "team": "CHAOS" }
            ]
            """);

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance, LiveRoster.Parse(doc.RootElement.Clone()))!;

        Assert.Equal("BOTTOM", stats.Position);
        Assert.Equal("Yasuo", stats.EnemyLaner);
        Assert.Equal("Yasuo", Map(stats)["enemyBot"]);
        Assert.Equal("Swain", Map(stats)["enemyMid"]);
        Assert.Equal(MatchupSources.Eog, stats.MatchupSource);
    }

    /// <summary>The player's own row in teams[] carries the lane even when
    /// localPlayer's does not: the capture reads it back, no estimate needed.</summary>
    [Fact]
    public void ExtractFromEog_ReadsTheLocalLaneFromTheTeamRows_WhenLocalPlayerHasNone()
    {
        var own = new[] { ("Teemo", "TOP"), ("Zaahen", "JUNGLE"), ("Riven", "MIDDLE"), ("Miss Fortune", "BOTTOM"), ("Pantheon", "UTILITY") };
        var enemy = new[] { ("Gragas", "TOP"), ("Hecarim", "JUNGLE"), ("Swain", "MIDDLE"), ("Yasuo", "BOTTOM"), ("Soraka", "UTILITY") };

        var stats = StatsExtractor.ExtractFromEog(Eog(own, enemy, me: "Miss Fortune"), NullLogger.Instance)!;

        Assert.Equal("BOTTOM", stats.Position);
        Assert.Equal("Yasuo", stats.EnemyLaner);
        Assert.Equal(MatchupSources.Eog, stats.MatchupSource);
    }

    // ── MatchupFallback.ApplyForGameEnd: the coordinator's gates ─────────────

    [Fact]
    public void ApplyForGameEnd_RecoveredGame_GetsNothing()
    {
        var game = BlankCapture();

        Assert.False(MatchupFallback.ApplyForGameEnd(game, isRecovered: true, sessionKey: "k", "BOTTOM", ChampSelectMap()));

        Assert.Equal("", game.Position);
        Assert.Equal("", game.ParticipantMap);
        Assert.Equal("", game.MatchupSource);
    }

    [Fact]
    public void ApplyForGameEnd_WithoutASessionKey_IgnoresTheSnapshot_AndEstimatesFromPriors()
    {
        var game = BlankCapture(me: "Jinx", own: ClearOwn, enemy: ClearEnemy);
        var stale = JsonSerializer.Serialize(new Dictionary<string, string> { ["ownMid"] = "Jinx", ["enemyMid"] = "Zed" });

        Assert.True(MatchupFallback.ApplyForGameEnd(game, isRecovered: false, sessionKey: null, "MIDDLE", stale));

        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal("Caitlyn", game.EnemyLaner);
        Assert.Equal(MatchupSources.Heuristic, game.MatchupSource);
    }

    [Fact]
    public void ApplyForGameEnd_WithASessionKey_UsesThisLobbysSnapshot()
    {
        var game = BlankCapture();

        Assert.True(MatchupFallback.ApplyForGameEnd(game, isRecovered: false, sessionKey: "k", "BOTTOM", ChampSelectMap()));

        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal("Yasuo", game.EnemyLaner);
        Assert.Equal(10, Map(game).Count);
        Assert.Equal(MatchupSources.ChampSelect, game.MatchupSource);
    }

    [Fact]
    public void ApplyForGameEnd_WithASessionKey_RefusesAForeignLobby_AndFallsBackToPriors()
    {
        var game = BlankCapture(me: "Jinx", own: ClearOwn, enemy: ClearEnemy);

        Assert.True(MatchupFallback.ApplyForGameEnd(game, isRecovered: false, sessionKey: "k", "BOTTOM", ChampSelectMap()));

        Assert.Equal("Caitlyn", game.EnemyLaner); // never Yasuo from the other lobby
        Assert.Equal(MatchupSources.Heuristic, game.MatchupSource);
    }

    [Fact]
    public void ApplyForGameEnd_CompleteCapture_IsLeftAlone()
    {
        var eog = Eog(Blank(OwnComp), Blank(EnemyComp), me: "Miss Fortune");
        var game = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance, LiveRoster.Parse(PlayerList()))!;

        Assert.False(MatchupFallback.ApplyForGameEnd(game, isRecovered: false, sessionKey: "k", "MIDDLE", ChampSelectMap()));

        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal(MatchupSources.Live, game.MatchupSource);
    }

    /// <summary>A lane the row already holds decides the opponent; the estimate
    /// only fills the gap, and the row is marked for confirmation even though the
    /// capture had stamped a confirmed source.</summary>
    [Fact]
    public void ApplyRolePriors_UsesTheRowsLane_AndMarksTheRowForConfirmation()
    {
        var game = BlankCapture(me: "Jinx", own: ClearOwn, enemy: ClearEnemy);
        game.Position = "MIDDLE";
        game.MatchupSource = MatchupSources.Live;

        Assert.True(MatchupFallback.ApplyRolePriors(game));

        Assert.Equal("MIDDLE", game.Position);
        Assert.Equal("Syndra", game.EnemyLaner); // the enemy mid, not the enemy bot
        Assert.Equal(MatchupSources.Heuristic, game.MatchupSource);
        Assert.True(MatchupSources.NeedsConfirmation(game.MatchupSource));
    }

    private static string ChampSelectMap() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["ownTop"] = "Teemo", ["ownJg"] = "Zaahen", ["ownMid"] = "Riven", ["ownBot"] = "Miss Fortune", ["ownSupp"] = "Pantheon",
        ["enemyTop"] = "Gragas", ["enemyJg"] = "Hecarim", ["enemyMid"] = "Swain", ["enemyBot"] = "Yasuo", ["enemySupp"] = "Soraka",
    });

    private static GameStats BlankCapture(string me = "Miss Fortune", string[]? own = null, string[]? enemy = null)
    {
        var eog = Eog(Blank(own ?? OwnComp), Blank(enemy ?? EnemyComp), me);
        return StatsExtractor.ExtractFromEog(eog, NullLogger.Instance)!;
    }

    // A comp whose role priors are unambiguous, so the estimate is deterministic
    // to read in a test. (The Miss Fortune lobby above is deliberately NOT: its
    // priors put Swain bot / Yasuo mid, which is exactly the kind of guess the
    // Match-V5 pass exists to correct.)
    private static readonly string[] ClearOwn = ["Garen", "Lee Sin", "Ahri", "Jinx", "Thresh"];
    private static readonly string[] ClearEnemy = ["Darius", "Vi", "Syndra", "Caitlyn", "Lulu"];

    [Fact]
    public void ApplyChampSelect_FillsLaneOpponentAndMap_AndMarksItAnEstimate()
    {
        var game = BlankCapture();

        Assert.True(MatchupFallback.ApplyChampSelect(game, "bottom", ChampSelectMap()));

        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal("Yasuo", game.EnemyLaner);
        Assert.Equal(10, Map(game).Count);
        Assert.Equal(MatchupSources.ChampSelect, game.MatchupSource);
        Assert.True(MatchupSources.NeedsConfirmation(game.MatchupSource));
    }

    [Fact]
    public void ApplyChampSelect_DerivesTheLaneFromTheOwnSlot_WhenNoPositionWasGiven()
    {
        var game = BlankCapture();
        Assert.True(MatchupFallback.ApplyChampSelect(game, "", ChampSelectMap()));
        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal("Yasuo", game.EnemyLaner);
    }

    /// <summary>A stale lobby (dodge, remake, app started mid-game) never labels this game.</summary>
    [Fact]
    public void ApplyChampSelect_RefusesASnapshotWithoutThePlayedChampion()
    {
        var game = BlankCapture(me: "Caitlyn");

        Assert.False(MatchupFallback.ApplyChampSelect(game, "BOTTOM", ChampSelectMap()));

        Assert.Equal("", game.Position);
        Assert.Equal("", game.ParticipantMap);
        Assert.Equal("", game.MatchupSource);
    }

    [Fact]
    public void ApplyChampSelect_NeverOverwritesWhatTheCaptureResolved()
    {
        var eog = Eog(Blank(OwnComp), Blank(EnemyComp), me: "Miss Fortune");
        var game = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance, LiveRoster.Parse(PlayerList()))!;
        var staleMap = JsonSerializer.Serialize(new Dictionary<string, string> { ["ownBot"] = "Miss Fortune", ["enemyBot"] = "Caitlyn" });

        Assert.False(MatchupFallback.ApplyChampSelect(game, "MIDDLE", staleMap));

        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal("Yasuo", game.EnemyLaner);
        Assert.Equal(MatchupSources.Live, game.MatchupSource);
    }

    [Fact]
    public void ApplyChampSelect_IgnoresGarbage()
    {
        var game = BlankCapture();
        Assert.False(MatchupFallback.ApplyChampSelect(game, "BOTTOM", ""));
        Assert.False(MatchupFallback.ApplyChampSelect(game, "BOTTOM", "not json"));
        Assert.False(MatchupFallback.ApplyChampSelect(null, "BOTTOM", ChampSelectMap()));
        Assert.Equal("", game.MatchupSource);
    }

    // ── MatchupFallback: role priors ─────────────────────────────────────────

    [Fact]
    public void ApplyRolePriors_EstimatesBothSidesFromTheComp()
    {
        var game = BlankCapture(me: "Jinx", own: ClearOwn, enemy: ClearEnemy);

        Assert.True(MatchupFallback.ApplyRolePriors(game));

        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal("Caitlyn", game.EnemyLaner);
        Assert.Equal(MatchupSources.Heuristic, game.MatchupSource);
        Assert.True(MatchupSources.NeedsConfirmation(game.MatchupSource));
        var map = Map(game);
        Assert.Equal(10, map.Count);
        Assert.Equal("Garen", map["ownTop"]);
        Assert.Equal("Lee Sin", map["ownJg"]);
        Assert.Equal("Ahri", map["ownMid"]);
        Assert.Equal("Jinx", map["ownBot"]);
        Assert.Equal("Thresh", map["ownSupp"]);
        Assert.Equal("Darius", map["enemyTop"]);
        Assert.Equal("Vi", map["enemyJg"]);
        Assert.Equal("Syndra", map["enemyMid"]);
        Assert.Equal("Caitlyn", map["enemyBot"]);
        Assert.Equal("Lulu", map["enemySupp"]);
        Assert.Equal("Jinx+Thresh vs Caitlyn+Lulu",
            MatchupDisplay.Build(game.ChampionName, game.EnemyLaner, game.Position, game.ParticipantMap));
    }

    /// <summary>The user's own lobby: the estimate is a real answer (a full map, a
    /// lane, an opponent) even where the priors pick differently from the truth —
    /// that is what the Match-V5 pass corrects, not a reason to show nothing.</summary>
    [Fact]
    public void ApplyRolePriors_AlwaysGivesAFullEstimateForARankedLobby()
    {
        var game = BlankCapture();

        Assert.True(MatchupFallback.ApplyRolePriors(game));

        Assert.Equal("BOTTOM", game.Position);
        Assert.NotEqual("", game.EnemyLaner);
        Assert.Equal(10, Map(game).Count);
        Assert.Equal(MatchupSources.Heuristic, game.MatchupSource);
    }

    [Fact]
    public void ApplyRolePriors_RefusesAnythingButAFullSummonersRiftLobby()
    {
        var aram = StatsExtractor.ExtractFromEog(
            Eog(Blank(OwnComp), Blank(EnemyComp), me: "Miss Fortune", gameMode: "ARAM", queueType: "ARAM_UNRANKED_5x5"),
            NullLogger.Instance)!;
        Assert.False(MatchupFallback.ApplyRolePriors(aram));
        Assert.Equal("", aram.MatchupSource);

        var shortLobby = StatsExtractor.ExtractFromEog(
            Eog(Blank("Teemo", "Miss Fortune"), Blank("Gragas", "Yasuo"), me: "Miss Fortune"),
            NullLogger.Instance)!;
        Assert.False(MatchupFallback.ApplyRolePriors(shortLobby));
        Assert.Equal("", shortLobby.Position);
    }

    /// <summary>After a database round-trip the champion lists come back as JSON
    /// elements, not lists; the estimate must read those too.</summary>
    [Fact]
    public async Task ApplyRolePriors_ReadsTheCompAfterADatabaseRoundTrip()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var captured = BlankCapture(me: "Jinx", own: ClearOwn, enemy: ClearEnemy);
        await scope.Games.SaveAsync(captured);
        var game = (await scope.Games.GetAsync(captured.GameId))!;
        // A queue outside every label fallback, set after the save (SaveAsync skips
        // non-ranked queues), so only the round-tripped raw game mode can admit the lobby.
        game.QueueType = "Custom";
        Assert.IsType<JsonElement>(game.RawStats["_own_champions"]);
        Assert.IsType<JsonElement>(game.RawStats["_game_mode_raw"]);

        Assert.True(MatchupFallback.ApplyRolePriors(game));

        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal("Caitlyn", game.EnemyLaner);
    }

    // ── Repository: the source column and the confirmation queue ─────────────

    [Fact]
    public async Task SaveAsync_RoundTripsTheMatchupSource()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var eog = Eog(Blank(OwnComp), Blank(EnemyComp), me: "Miss Fortune");
        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance, LiveRoster.Parse(PlayerList()))!;

        await scope.Games.SaveAsync(stats);

        var game = (await scope.Games.GetAsync(stats.GameId))!;
        Assert.Equal(MatchupSources.Live, game.MatchupSource);
        Assert.Equal("Yasuo", game.EnemyLaner);
        Assert.Equal("BOTTOM", game.Position);
        Assert.Equal(10, Map(game).Count);
    }

    [Fact]
    public async Task ConfirmationQueue_HoldsEstimates_AndReleasesConfirmedRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var map = ChampSelectMap();

        async Task Seed(long id, string source, string enemy = "Yasuo", string participantMap = "")
        {
            var g = TestGameStatsFactory.Create(id, champion: "Miss Fortune");
            g.Position = "BOTTOM";
            g.EnemyLaner = enemy;
            g.ParticipantMap = participantMap;
            g.MatchupSource = source;
            await scope.Games.SaveAsync(g);
        }

        await Seed(1, MatchupSources.Heuristic, participantMap: map);
        await Seed(2, MatchupSources.ChampSelect, participantMap: map);
        await Seed(3, MatchupSources.History, enemy: "", participantMap: map);
        await Seed(4, MatchupSources.Live, participantMap: map);
        await Seed(5, MatchupSources.Eog, participantMap: map);
        await Seed(6, MatchupSources.MatchV5, participantMap: map);
        await Seed(7, "", participantMap: map);              // legacy, both columns filled → done
        await Seed(8, "", participantMap: "");               // legacy, map missing → queued

        var queued = await scope.Games.GetGameIdsMissingEnemyLanerAsync();

        Assert.Equal(new long[] { 1, 2, 3, 8 }, queued.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task UpdateMatchupAsync_KeepsAColumnWhenTheNewValueIsBlank_AndStampsTheSource()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var g = TestGameStatsFactory.Create(11, champion: "Miss Fortune");
        g.EnemyLaner = "Yasuo";
        g.ParticipantMap = ChampSelectMap();
        g.MatchupSource = MatchupSources.Heuristic;
        await scope.Games.SaveAsync(g);

        await scope.Games.UpdateMatchupAsync(11, "", "", "", MatchupSources.MatchV5);
        var kept = (await scope.Games.GetAsync(11))!;
        Assert.Equal("Yasuo", kept.EnemyLaner);
        Assert.Equal(ChampSelectMap(), kept.ParticipantMap);
        Assert.Equal(MatchupSources.MatchV5, kept.MatchupSource);

        await scope.Games.UpdateMatchupAsync(11, "Caitlyn", "", "", MatchupSources.MatchV5);
        Assert.Equal("Caitlyn", (await scope.Games.GetAsync(11))!.EnemyLaner);
        Assert.Empty(await scope.Games.GetGameIdsMissingEnemyLanerAsync());
    }

    /// <summary>The player typing the opponent on the Review page outranks every
    /// estimate: the row leaves the queue and Match-V5 never rewrites it.</summary>
    [Fact]
    public async Task UpdateEnemyLanerAsync_IsThePlayersWord_AndLeavesTheQueue()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var g = TestGameStatsFactory.Create(12, champion: "Miss Fortune");
        g.EnemyLaner = "Swain";
        g.ParticipantMap = ChampSelectMap();
        g.MatchupSource = MatchupSources.Heuristic;
        await scope.Games.SaveAsync(g);
        Assert.Contains(12L, await scope.Games.GetGameIdsMissingEnemyLanerAsync());

        await scope.Games.UpdateEnemyLanerAsync(12, "Yasuo");

        var row = (await scope.Games.GetAsync(12))!;
        Assert.Equal("Yasuo", row.EnemyLaner);
        Assert.Equal(MatchupSources.User, row.MatchupSource);
        Assert.True(MatchupSources.IsConfirmed(row.MatchupSource));
        Assert.DoesNotContain(12L, await scope.Games.GetGameIdsMissingEnemyLanerAsync());

        // Clearing it hands the opponent back to automation: queued again, no 'user' stamp.
        await scope.Games.UpdateEnemyLanerAsync(12, "");
        Assert.Contains(12L, await scope.Games.GetGameIdsMissingEnemyLanerAsync());
        Assert.Equal("", (await scope.Games.GetAsync(12))!.MatchupSource);
    }

    // ── Match-V5 confirms an estimate ────────────────────────────────────────

    private sealed class FakeMatchClient : IRiotMatchClient
    {
        public JsonElement? Match { get; set; }
        public List<string> Requested { get; } = [];

        public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
        {
            Requested.Add(matchId);
            return Task.FromResult(Match);
        }

        public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(null);
    }

    private static JsonElement MatchV5(params (string Puuid, int Team, string Position, string Champ)[] rows)
    {
        var participants = string.Join(",", rows.Select(r =>
            $$$"""{"puuid":"{{{r.Puuid}}}","teamId":{{{r.Team}}},"teamPosition":"{{{r.Position}}}","championName":"{{{r.Champ}}}"}"""));
        return JsonDocument.Parse($$$"""{"info":{"participants":[{{{participants}}}]}}""").RootElement;
    }

    /// <summary>The role-prior estimate put the wrong opponent on the row; Riot's
    /// record corrects it and the row leaves the queue. This is exactly the
    /// coordinator's +90s pass (BackfillGameAsync on the fresh game).</summary>
    [Fact]
    public async Task BackfillGameAsync_CorrectsAnEstimate_AndStampsMatchV5()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var game = BlankCapture();
        Assert.True(MatchupFallback.ApplyRolePriors(game));
        game.EnemyLaner = "Swain"; // pretend the priors got the bot laner wrong
        game.Puuid = "self";
        await scope.Games.SaveAsync(game);
        Assert.Contains(game.GameId, await scope.Games.GetGameIdsMissingEnemyLanerAsync());

        var client = new FakeMatchClient
        {
            Match = MatchV5(
                ("self", 100, "BOTTOM", "MissFortune"), ("p2", 100, "UTILITY", "Pantheon"),
                ("p3", 200, "BOTTOM", "Yasuo"), ("p4", 200, "UTILITY", "Soraka"), ("p5", 200, "MIDDLE", "Swain")),
        };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = "self" });
        var service = new EnemyLanerBackfillService(scope.Games, client, config, NullLogger<EnemyLanerBackfillService>.Instance);

        Assert.Equal(EnemyLanerBackfillOutcome.Updated, await service.BackfillGameAsync(game.GameId));

        var confirmed = (await scope.Games.GetAsync(game.GameId))!;
        Assert.Equal("Yasuo", confirmed.EnemyLaner);
        Assert.Equal(MatchupSources.MatchV5, confirmed.MatchupSource);
        Assert.Equal("Swain", Map(confirmed)["enemyMid"]);
        Assert.DoesNotContain(game.GameId, await scope.Games.GetGameIdsMissingEnemyLanerAsync());
    }

    /// <summary>Riot's teamPosition corrects a lane game end estimated wrong; the
    /// matchv5 stamp never freezes a guessed lane.</summary>
    [Fact]
    public async Task BackfillGameAsync_CorrectsAnEstimatedLane()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var game = BlankCapture();
        Assert.True(MatchupFallback.ApplyRolePriors(game));
        game.Position = "MIDDLE"; // a wrong lane estimate
        await scope.Games.SaveAsync(game);

        var client = new FakeMatchClient
        {
            Match = MatchV5(("self", 100, "BOTTOM", "MissFortune"), ("p3", 200, "BOTTOM", "Yasuo")),
        };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = "self" });
        var service = new EnemyLanerBackfillService(scope.Games, client, config, NullLogger<EnemyLanerBackfillService>.Instance);

        Assert.Equal(EnemyLanerBackfillOutcome.Updated, await service.BackfillGameAsync(game.GameId));

        var row = (await scope.Games.GetAsync(game.GameId))!;
        Assert.Equal("BOTTOM", row.Position);
        Assert.Equal("Yasuo", row.EnemyLaner);
        Assert.Equal(MatchupSources.MatchV5, row.MatchupSource);
    }

    /// <summary>An opponent the player typed survives Match-V5; the map and the
    /// lane still fill in, and the row leaves the queue.</summary>
    [Fact]
    public async Task BackfillGameAsync_KeepsThePlayersTypedOpponent_ButFillsTheMapAndLane()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var g = TestGameStatsFactory.Create(13, champion: "Miss Fortune");
        g.Position = "";
        g.EnemyLaner = "";
        g.ParticipantMap = "";
        await scope.Games.SaveAsync(g);
        await scope.Games.UpdateEnemyLanerAsync(13, "Draven");
        Assert.Contains(13L, await scope.Games.GetGameIdsMissingEnemyLanerAsync()); // the map is still blank

        var client = new FakeMatchClient
        {
            Match = MatchV5(("self", 100, "BOTTOM", "MissFortune"), ("p3", 200, "BOTTOM", "Yasuo")),
        };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = "self" });
        var service = new EnemyLanerBackfillService(scope.Games, client, config, NullLogger<EnemyLanerBackfillService>.Instance);

        Assert.Equal(EnemyLanerBackfillOutcome.Updated, await service.BackfillGameAsync(13));

        var row = (await scope.Games.GetAsync(13))!;
        Assert.Equal("Draven", row.EnemyLaner);
        Assert.Equal(MatchupSources.User, row.MatchupSource);
        Assert.Equal("BOTTOM", row.Position);
        Assert.Equal("Yasuo", Map(row)["enemyBot"]);
        Assert.DoesNotContain(13L, await scope.Games.GetGameIdsMissingEnemyLanerAsync());
    }

    /// <summary>The bulk sweep (Settings button, startup heal) picks estimates up too.</summary>
    [Fact]
    public async Task RunAsync_SweepsEstimatedRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var game = BlankCapture();
        Assert.True(MatchupFallback.ApplyChampSelect(game, "BOTTOM", ChampSelectMap()));
        game.Puuid = "self";
        await scope.Games.SaveAsync(game);

        var client = new FakeMatchClient
        {
            Match = MatchV5(("self", 100, "BOTTOM", "MissFortune"), ("p3", 200, "BOTTOM", "Yasuo")),
        };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = "self" });
        var service = new EnemyLanerBackfillService(scope.Games, client, config, NullLogger<EnemyLanerBackfillService>.Instance);

        var result = await service.RunAsync(maxGames: 10);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Updated);
        Assert.Equal(MatchupSources.MatchV5, (await scope.Games.GetAsync(game.GameId))!.MatchupSource);
        Assert.Equal(0, (await service.RunAsync(maxGames: 10)).Scanned); // confirmed rows are not re-fetched
        Assert.Single(client.Requested);
    }
}

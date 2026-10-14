using System.Text;
using System.Text.Json;
using Revu.Core.Models;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>The markers document v1 and its privacy rules (contract 6.3).</summary>
public sealed class VodShareMarkersTests
{
    internal static VodEventDto Event(long id, string type, int t, string label, string kind = "neutral",
        string summary = "", string encounterNote = "", string objectiveTitle = "", bool removed = false) =>
        new(id, type, t, $"{t / 60}:{t % 60:00}", type[..Math.Min(3, type.Length)], label, summary, kind, "#ffffff",
            ObjectiveId: objectiveTitle.Length > 0 ? 9 : null, ObjectiveTitle: objectiveTitle,
            EncounterNote: encounterNote, Removed: removed);

    internal static VodBookmarkDto Bookmark(long id, int t, string note, bool clip = false, int? end = null) =>
        new(id, t, $"{t / 60}:{t % 60:00}", note, "[]", clip, clip ? t : null, end);

    internal static VodDto Vod(IEnumerable<VodEventDto>? events = null, IEnumerable<VodBookmarkDto>? bookmarks = null,
        double videoStart = 0, string champion = "Jinx", string enemy = "Caitlyn") =>
        new("2026-10-14T00:00:00", true, 123, @"C:\rec\capture.mp4", "capture.mp4", champion, enemy, "Victory", "#00ff00",
            "Ranked Solo", "Oct 12, 2026", 1834, (bookmarks ?? []).ToList(), (events ?? []).ToList(), [], [],
            GameTimeAtVideoStart: videoStart);

    private static GameStats Game(bool win = true, bool remake = false) => new()
    {
        GameId = 123,
        ChampionName = "Jinx",
        EnemyLaner = "Caitlyn",
        Position = "BOTTOM",
        Role = "CARRY",
        Win = win,
        IsRemake = remake,
        GameDuration = 1834,
        Timestamp = new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        SummonerName = "SecretSummoner#NA1",
        Puuid = "puuid-secret",
        ReviewNotes = "private review note",
    };

    private static JsonElement Parse(VodShareMarkers.Result result) => JsonDocument.Parse(result.Json).RootElement.Clone();

    [Fact]
    public void TheDocument_HasTheV1Shape()
    {
        var vod = Vod([Event(1, "DRAGON", 312, "Dragon", "gold"), Event(2, "KILL", 100, "Kill", "win")], videoStart: -12.345);
        var doc = Parse(VodShareMarkers.Build(vod, Game(), includeNotes: false));

        Assert.Equal(new[] { "version", "gameTimeAtVideoStart", "game", "markers", "notes" },
            doc.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, doc.GetProperty("version").GetInt32());
        Assert.Equal(-12.35, doc.GetProperty("gameTimeAtVideoStart").GetDouble());
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("notes").ValueKind);
        var game = doc.GetProperty("game");
        Assert.Equal("Jinx", game.GetProperty("champion").GetString());
        Assert.Equal("Caitlyn", game.GetProperty("opponentChampion").GetString());
        Assert.Equal("BOTTOM", game.GetProperty("role").GetString());
        Assert.Equal("win", game.GetProperty("result").GetString());
        Assert.Equal(1834, game.GetProperty("durationS").GetInt32());
        Assert.Matches(@"^2026-10-1[23]$", game.GetProperty("playedAt").GetString());
        var markers = doc.GetProperty("markers").EnumerateArray().ToList();
        Assert.Equal(new[] { 100.0, 312.0 }, markers.Select(m => m.GetProperty("t").GetDouble()));
        Assert.Equal("{\"t\":312,\"kind\":\"DRAGON\",\"label\":\"Dragon\",\"tone\":\"gold\"}", markers[1].GetRawText());
    }

    [Fact]
    public void Markers_NeverCarryFreeTextOrPrivateData()
    {
        var vod = Vod([
            Event(1, "DEATH", 400, "Death", "loss", summary: "Killed by EnemyPlayer#EUW", encounterNote: "my private note",
                objectiveTitle: "Ward before river"),
            Event(2, "KILL", 410, "Kill", "win", removed: true),
        ], [Bookmark(5, 500, "secret bookmark note")]);
        var result = VodShareMarkers.Build(vod, Game(), includeNotes: false);

        foreach (var text in new[] { "EnemyPlayer", "my private note", "Ward before river", "secret bookmark note",
                     "SecretSummoner", "puuid-secret", "private review note", "capture.mp4", "objective" })
            Assert.DoesNotContain(text, result.Json, StringComparison.OrdinalIgnoreCase);
        // The removed (ghost) event is not a marker; bookmarks never become markers.
        Assert.Equal(1, result.Markers);
        Assert.Equal(0, result.Notes);
    }

    [Fact]
    public void Kinds_AreNormalized_LabelsCut_AndOnlyKnownTonesKept()
    {
        var vod = Vod([
            Event(1, "spell-flash", 10, "  Flash\u0007 used  ", "purple"),
            Event(2, "!!!", 20, "Nothing", "win"),
            Event(3, "OBJECTIVE_" + new string('X', 40), 30, new string('L', 60), "win"),
            Event(4, "KILL", 40, "   ", "loss"),
            Event(5, "KILL", -5, "Before start", "win"),
            Event(6, "KILL", 20000, "After the end", "win"),
        ]);
        var markers = Parse(VodShareMarkers.Build(vod, null, false)).GetProperty("markers").EnumerateArray().ToList();

        Assert.Equal(3, markers.Count);
        Assert.Equal(("SPELLFLASH", "Flash used"), (markers[0].GetProperty("kind").GetString(), markers[0].GetProperty("label").GetString()));
        Assert.False(markers[0].TryGetProperty("tone", out _));
        Assert.Equal(32, markers[1].GetProperty("kind").GetString()!.Length);
        Assert.Equal(40, markers[1].GetProperty("label").GetString()!.Length);
        Assert.Equal(("KILL", "KILL", "loss"), (markers[2].GetProperty("kind").GetString(),
            markers[2].GetProperty("label").GetString(), markers[2].GetProperty("tone").GetString()));
    }

    [Fact]
    public void Notes_OnlyWhenTicked_FromBookmarksAndClips()
    {
        var vod = Vod([Event(1, "KILL", 100, "Kill")],
            [Bookmark(1, 655, "  "), Bookmark(2, 400, "Should have backed here\nreally", clip: true, end: 430)]);

        Assert.Equal(JsonValueKind.Null, Parse(VodShareMarkers.Build(vod, Game(), false)).GetProperty("notes").ValueKind);

        var result = VodShareMarkers.Build(vod, Game(), includeNotes: true);
        var notes = Parse(result).GetProperty("notes").EnumerateArray().ToList();
        Assert.Equal(2, result.Notes);
        Assert.Equal("{\"t\":400,\"end\":430,\"kind\":\"clip\",\"text\":\"Should have backed here really\"}", notes[0].GetRawText());
        Assert.Equal("{\"t\":655,\"kind\":\"bookmark\",\"text\":\"\"}", notes[1].GetRawText());
        Assert.Equal(1, result.Markers);
    }

    [Fact]
    public void TooManyMarkers_AreThinnedByDroppingEverySecondOne()
    {
        var events = Enumerable.Range(0, 1500).Select(i => Event(i, "WARD_PLACED", i, "Ward placed by the jungle", "neutral")).ToList();
        var result = VodShareMarkers.Build(Vod(events), null, false);
        var markers = Parse(result).GetProperty("markers").EnumerateArray().ToList();

        Assert.True(Encoding.UTF8.GetByteCount(Parse(result).GetProperty("markers").GetRawText()) <= 48000);
        Assert.True(markers.Count <= 1000);
        Assert.Equal(1500 - markers.Count, result.MarkersDropped);
        // Even spacing: the kept markers are the even indexes of each round.
        Assert.Equal(0, markers[0].GetProperty("t").GetInt32());
        Assert.Equal(markers[1].GetProperty("t").GetInt32() * 2, markers[2].GetProperty("t").GetInt32());
    }

    [Fact]
    public void LargeNotes_AreTrimmedTo280_ThenDroppedFromTheEnd()
    {
        var bookmarks = Enumerable.Range(0, 60).Select(i => Bookmark(i, i * 10, new string('n', 500))).ToList();
        var result = VodShareMarkers.Build(Vod(bookmarks: bookmarks), null, includeNotes: true);
        var notes = Parse(result).GetProperty("notes");

        Assert.True(Encoding.UTF8.GetByteCount(notes.GetRawText()) <= 14000);
        Assert.All(notes.EnumerateArray(), n => Assert.Equal(280, n.GetProperty("text").GetString()!.Length));
        Assert.Equal(0, notes[0].GetProperty("t").GetInt32());
        Assert.True(result.NotesDropped > 0);
        Assert.Equal(60, result.Notes + result.NotesDropped);
    }

    [Fact]
    public void MoreThan200Notes_AreCapped()
    {
        var bookmarks = Enumerable.Range(0, 250).Select(i => Bookmark(i, i, "b")).ToList();
        var result = VodShareMarkers.Build(Vod(bookmarks: bookmarks), null, includeNotes: true);
        Assert.Equal(200, result.Notes);
    }

    [Theory]
    [InlineData(-700, -600)]
    [InlineData(9000, 7200)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(3.14159, 3.14)]
    public void VideoStart_IsClampedAndRounded(double value, double expected)
    {
        Assert.Equal(expected, VodShareMarkers.VideoStart(value));
    }

    [Fact]
    public void GameMeta_RemakeAndMissingValues()
    {
        var remake = Parse(VodShareMarkers.Build(Vod(), Game(remake: true), false)).GetProperty("game");
        Assert.Equal("remake", remake.GetProperty("result").GetString());
        Assert.Equal("loss", Parse(VodShareMarkers.Build(Vod(), Game(win: false), false)).GetProperty("game").GetProperty("result").GetString());

        // No game row: champions from the snapshot, no result.
        var bare = Parse(VodShareMarkers.Build(Vod(champion: "Ahri", enemy: ""), null, false)).GetProperty("game");
        Assert.Equal("Ahri", bare.GetProperty("champion").GetString());
        Assert.False(bare.TryGetProperty("opponentChampion", out _));
        Assert.False(bare.TryGetProperty("result", out _));
        Assert.False(bare.TryGetProperty("role", out _));
    }

    [Fact]
    public void TheWholeBody_StaysUnderTheProxyCap()
    {
        var events = Enumerable.Range(0, 3000).Select(i => Event(i, "KILL", i % 3600, new string('k', 40), "win")).ToList();
        var bookmarks = Enumerable.Range(0, 300).Select(i => Bookmark(i, i * 10, new string('é', 500), clip: true, end: i * 10 + 5)).ToList();
        var result = VodShareMarkers.Build(Vod(events, bookmarks), Game(), includeNotes: true);
        Assert.True(Encoding.UTF8.GetByteCount(result.Json) <= 65536);
    }
}

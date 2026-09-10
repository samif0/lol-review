using System.Text.Json.Nodes;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class EventIdentityTests
{
    private static GameEvent Ev(string type, int t, string details = "{}", string? key = null) =>
        new() { GameId = 1, EventType = type, GameTimeS = t, Details = details, EventKey = key };

    private static EventCorrection Corr(string op, string subjectKey, string subjectType, int subjectTime,
        EventPatch? patch = null, EventOriginal? original = null) => new(
        Id: 1, CorrectionId: Guid.NewGuid().ToString("D"), GameId: 1, SubjectKey: subjectKey, SubjectType: subjectType,
        SubjectTimeS: subjectTime, Op: op, Patch: patch ?? EventPatch.Empty,
        Original: original ?? new EventOriginal(subjectType, subjectTime, "{}"), Reason: "", Detector: "live",
        DetectorVersion: null, AppVersion: "", SupersedesId: null, RebasedFrom: "", DeltaS: null,
        State: CorrectionStates.Active, AppliedEventId: null, AppliedAt: null, ApplyError: "", ShareState: "held",
        SharedAt: null, CreatedAt: 0, UpdatedAt: 0);

    private static Dictionary<string, JsonNode?> Attrs(params (string Key, object Value)[] pairs)
    {
        var d = new Dictionary<string, JsonNode?>();
        foreach (var (k, v) in pairs) d[k] = JsonValue.Create(v);
        return d;
    }

    [Theory]
    [InlineData("KILL", "{\"victim\":\"Lee Sin\"}", "det:KILL:100:Lee Sin")]
    [InlineData("DEATH", "{\"killer\":\"Ahri\",\"assisters\":[\"Lee Sin\"]}", "det:DEATH:100:Ahri")]
    [InlineData("ASSIST", "{\"killer\":\"Ahri\",\"victim\":\"Jinx\"}", "det:ASSIST:100:Ahri>Jinx")]
    [InlineData("DRAGON", "{\"dragon_type\":\"Infernal\",\"stolen\":false}", "det:DRAGON:100:Infernal")]
    [InlineData("TURRET", "{\"turret\":\"Turret_T2_L_03_A\",\"killer\":\"Ahri\"}", "det:TURRET:100:Turret_T2_L_03_A")]
    [InlineData("JUNGLE_PROXIMITY", "{\"who\":\"enemy\",\"champion\":\"Lee Sin\"}", "det:JUNGLE_PROXIMITY:100:enemy")]
    [InlineData("BARON", "{\"stolen\":false,\"killer\":\"Ahri\"}", "det:BARON:100:")]
    [InlineData("INHIBITOR", "{\"inhib\":\"Barracks_T1_L1\"}", "det:INHIBITOR:100:")]
    [InlineData("kill", "{\"victim\":\" Lee Sin \"}", "det:KILL:100:Lee Sin")]
    public void KeyFor_UsesTypeAnchorAndDisc_PerType(string type, string details, string expected)
    {
        Assert.Equal(expected, EventIdentity.KeyFor(Ev(type, 100, details)));
    }

    [Fact]
    public void KeyFor_TeamfightAnchorsOnStartS()
    {
        var fight = Ev("TEAMFIGHT", 610, "{\"start_s\":600,\"end_s\":640,\"self\":\"in\"}");
        Assert.Equal("det:TEAMFIGHT:600:", EventIdentity.KeyFor(fight));
        Assert.Equal(600, EventIdentity.AnchorOf(fight));
        Assert.Equal("det:TEAMFIGHT:610:", EventIdentity.KeyFor(Ev("TEAMFIGHT", 610, "{}")));
    }

    [Fact]
    public void KeyFor_IsStableWhenDetectorStampsLand()
    {
        var before = Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"assisters\":[]}");
        var after = Ev("DEATH", 812,
            "{\"killer\":\"Lee Sin\",\"assisters\":[],\"jungle_gank\":true,\"map_state\":true,\"fog_death\":true,\"enemy_jg_dark_s\":74}");
        Assert.Equal(EventIdentity.KeyFor(before), EventIdentity.KeyFor(after));
    }

    [Fact]
    public void KeyFor_EscapesColonAndHashInNames()
    {
        var key = EventIdentity.KeyFor(Ev("KILL", 100, "{\"victim\":\"Bot#1:Two\"}"));
        Assert.Equal("det:KILL:100:Bot%231%3ATwo", key);
        Assert.Equal(4, key.Split(':').Length);
    }

    [Theory]
    [InlineData("det:KILL:100:Bot%231%3ATwo", "KILL", 100, "Bot#1:Two", 1)]
    [InlineData("det:DEATH:812:Lee Sin#2", "DEATH", 812, "Lee Sin", 2)]
    [InlineData("det:BARON:1500:", "BARON", 1500, "", 1)]
    [InlineData("det:BARON:1500:#3", "BARON", 1500, "", 3)]
    public void Parse_RoundTrips(string key, string type, int anchor, string disc, int ordinal)
    {
        var parsed = EventIdentity.Parse(key);
        Assert.NotNull(parsed);
        Assert.Equal((type, anchor, disc, ordinal), parsed!.Value);
        Assert.Null(EventIdentity.Parse("usr:2c6b0a7e-2f1d-4d4a-9a1e-0f2b5c6d7e80"));
        Assert.Null(EventIdentity.Parse("det:KILL:nope:x"));
    }

    [Fact]
    public void KeyForBatch_SuffixesSameBatchCollisions_InTimeThenIndexOrder()
    {
        var batch = new List<GameEvent>
        {
            Ev("KILL", 200, "{\"victim\":\"Jinx\"}"),        // 0: later in time, first in batch
            Ev("KILL", 100, "{\"victim\":\"Jinx\"}"),        // 1
            Ev("KILL", 100, "{\"victim\":\"Jinx\"}"),        // 2: same key, same second
            Ev("DEATH", 100, "{\"killer\":\"Jinx\"}"),       // 3: different type, no collision
            Ev("TRADE", 300, "{}", "usr:11111111-1111-1111-1111-111111111111"), // 4: keeps its usr key
            Ev("KILL", 100, "{\"victim\":\"Jinx\"}"),        // 5: third at that second
        };
        var keys = EventIdentity.KeyForBatch(batch);
        Assert.Equal("det:KILL:200:Jinx", keys[0]);
        Assert.Equal("det:KILL:100:Jinx", keys[1]);
        Assert.Equal("det:KILL:100:Jinx#2", keys[2]);
        Assert.Equal("det:DEATH:100:Jinx", keys[3]);
        Assert.Equal("usr:11111111-1111-1111-1111-111111111111", keys[4]);
        Assert.Equal("det:KILL:100:Jinx#3", keys[5]);
        Assert.Equal(keys, EventIdentity.KeyForBatch(batch));
    }

    [Fact]
    public void FindMatch_ExactKeyWins()
    {
        var cands = new List<GameEvent> { Ev("DEATH", 811, "{\"killer\":\"Lee Sin\"}"), Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}") };
        var keys = EventIdentity.KeyForBatch(cands);
        var c = Corr(CorrectionOps.Attr, "det:DEATH:812:Lee Sin", "DEATH", 812);
        var m = EventIdentity.FindMatch(c, cands, keys, new HashSet<int>());
        Assert.NotNull(m);
        Assert.Equal(1, m!.Index);
        Assert.True(m.Exact);
        Assert.Equal("det:DEATH:812:Lee Sin", m.CandidateKey);
    }

    [Fact]
    public void FindMatch_FuzzyWithinTolerance_RequiresDiscAgreement()
    {
        var cands = new List<GameEvent> { Ev("DEATH", 814, "{\"killer\":\"Ahri\"}"), Ev("DEATH", 813, "{\"killer\":\"Lee Sin\"}") };
        var keys = EventIdentity.KeyForBatch(cands);
        var c = Corr(CorrectionOps.Attr, "det:DEATH:812:Lee Sin", "DEATH", 812);
        var m = EventIdentity.FindMatch(c, cands, keys, new HashSet<int>());
        Assert.NotNull(m);
        Assert.Equal(1, m!.Index);
        Assert.False(m.Exact);
        Assert.Equal("det:DEATH:813:Lee Sin", m.CandidateKey);

        // An empty discriminator on either side agrees with anything.
        var bare = new List<GameEvent> { Ev("DEATH", 813, "{}") };
        Assert.NotNull(EventIdentity.FindMatch(c, bare, EventIdentity.KeyForBatch(bare), new HashSet<int>()));

        // Outside the tolerance: nothing.
        var far = new List<GameEvent> { Ev("DEATH", 815, "{\"killer\":\"Lee Sin\"}") };
        Assert.Null(EventIdentity.FindMatch(c, far, EventIdentity.KeyForBatch(far), new HashSet<int>()));

        // A wider family uses its own tolerance.
        var fights = new List<GameEvent> { Ev("TEAMFIGHT", 609, "{\"start_s\":609,\"end_s\":640}") };
        var fc = Corr(CorrectionOps.Attr, "det:TEAMFIGHT:600:", "TEAMFIGHT", 600);
        Assert.NotNull(EventIdentity.FindMatch(fc, fights, EventIdentity.KeyForBatch(fights), new HashSet<int>()));
    }

    [Fact]
    public void FindMatch_TieYieldsNull()
    {
        var cands = new List<GameEvent> { Ev("DEATH", 811, "{}"), Ev("DEATH", 813, "{}") };
        var c = Corr(CorrectionOps.Attr, "det:DEATH:812:Lee Sin", "DEATH", 812);
        Assert.Null(EventIdentity.FindMatch(c, cands, EventIdentity.KeyForBatch(cands), new HashSet<int>()));
    }

    [Fact]
    public void FindMatch_NeverCrossesTypes()
    {
        var cands = new List<GameEvent> { Ev("KILL", 812, "{\"victim\":\"Lee Sin\"}"), Ev("ASSIST", 812, "{}") };
        var c = Corr(CorrectionOps.Attr, "det:DEATH:812:Lee Sin", "DEATH", 812);
        Assert.Null(EventIdentity.FindMatch(c, cands, EventIdentity.KeyForBatch(cands), new HashSet<int>()));
    }

    [Fact]
    public void FindMatch_RecognisesTheCorrectedSide()
    {
        // The detector now reproduces a retime from 812 to 820: the twin sits at the corrected time.
        var cands = new List<GameEvent> { Ev("DEATH", 820, "{\"killer\":\"Lee Sin\"}") };
        var c = Corr(CorrectionOps.Retime, "det:DEATH:812:Lee Sin", "DEATH", 812, new EventPatch(null, 820, null, null));
        var m = EventIdentity.FindMatch(c, cands, EventIdentity.KeyForBatch(cands), new HashSet<int>());
        Assert.NotNull(m);
        Assert.Equal(0, m!.Index);

        // And a retype KILL -> DEATH matches a DEATH at the same second even though the
        // discriminators name different people.
        var retyped = new List<GameEvent> { Ev("DEATH", 500, "{\"killer\":\"Ahri\"}") };
        var rc = Corr(CorrectionOps.Retype, "det:KILL:500:Jinx", "KILL", 500, new EventPatch("DEATH", null, null, null));
        Assert.NotNull(EventIdentity.FindMatch(rc, retyped, EventIdentity.KeyForBatch(retyped), new HashSet<int>()));

        // An add matches its detector twin on type and time alone.
        var added = new List<GameEvent> { Ev("DRAGON", 1201, "{\"dragon_type\":\"Ocean\"}") };
        var ac = Corr(CorrectionOps.Add, "usr:2c6b0a7e-2f1d-4d4a-9a1e-0f2b5c6d7e80", "DRAGON", 1200,
            new EventPatch("DRAGON", 1200, null, null), EventOriginal.None);
        Assert.NotNull(EventIdentity.FindMatch(ac, added, EventIdentity.KeyForBatch(added), new HashSet<int>()));
    }

    [Fact]
    public void FindMatch_ClaimedCandidatesAreSkipped()
    {
        var cands = new List<GameEvent> { Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"), Ev("DEATH", 813, "{\"killer\":\"Lee Sin\"}") };
        var keys = EventIdentity.KeyForBatch(cands);
        var c = Corr(CorrectionOps.Attr, "det:DEATH:812:Lee Sin", "DEATH", 812);
        var m = EventIdentity.FindMatch(c, cands, keys, new HashSet<int> { 0 });
        Assert.NotNull(m);
        Assert.Equal(1, m!.Index);
        Assert.False(m.Exact);
        Assert.Null(EventIdentity.FindMatch(c, cands, keys, new HashSet<int> { 0, 1 }));
    }

    [Fact]
    public void FindMatch_IgnoresMarkedCandidates()
    {
        var marked = "{\"killer\":\"Lee Sin\",\"correction\":{\"id\":\"x\",\"op\":\"attr\",\"attrs\":[]}}";
        var reviewed = "{\"source\":\"reviewed_encounter\",\"kind\":\"short\"}";
        var cands = new List<GameEvent> { Ev("DEATH", 812, marked), Ev("TRADE", 300, reviewed) };
        var keys = EventIdentity.KeyForBatch(cands);
        Assert.Null(EventIdentity.FindMatch(Corr(CorrectionOps.Attr, "det:DEATH:812:Lee Sin", "DEATH", 812), cands, keys, new HashSet<int>()));
        Assert.Null(EventIdentity.FindMatch(Corr(CorrectionOps.Attr, "det:TRADE:300:", "TRADE", 300), cands, keys, new HashSet<int>()));
    }

    [Fact]
    public void Satisfies_TypeTimeAndCorrectedAttrsOnly()
    {
        var original = new EventOriginal("DEATH", 812, "{\"killer\":\"Lee Sin\",\"fog_death\":true}");
        var patch = new EventPatch(null, 815, null, Attrs(("fog_death", false)), Confirmed: true);

        // A missing key equals false; time within 1 s; other stamps are not compared.
        Assert.True(EventIdentity.Satisfies(Ev("DEATH", 816, "{\"killer\":\"Lee Sin\",\"map_state\":true}"), patch, original));
        Assert.False(EventIdentity.Satisfies(Ev("DEATH", 817, "{\"killer\":\"Lee Sin\"}"), patch, original));
        Assert.False(EventIdentity.Satisfies(Ev("DEATH", 815, "{\"fog_death\":true}"), patch, original));
        Assert.False(EventIdentity.Satisfies(Ev("KILL", 815, "{}"), patch, original));

        // Type comes from the patch when it retypes, and end_s is compared within 1 s.
        var fightPatch = new EventPatch("TEAMFIGHT", 600, 640, Attrs(("verdict", "Down")));
        var fightOriginal = new EventOriginal("TEAMFIGHT", 598, "{\"start_s\":598,\"end_s\":630,\"verdict\":\"even\"}");
        Assert.True(EventIdentity.Satisfies(Ev("TEAMFIGHT", 601, "{\"start_s\":601,\"end_s\":641,\"verdict\":\"down\"}"), fightPatch, fightOriginal));
        Assert.False(EventIdentity.Satisfies(Ev("TEAMFIGHT", 601, "{\"start_s\":601,\"end_s\":645,\"verdict\":\"down\"}"), fightPatch, fightOriginal));

        Assert.Equal("", EventIdentity.Normalize(null));
        Assert.Equal("", EventIdentity.Normalize(JsonValue.Create(false)));
        Assert.Equal("", EventIdentity.Normalize(JsonValue.Create("")));
        Assert.Equal("true", EventIdentity.Normalize(JsonValue.Create(true)));
        Assert.Equal("short", EventIdentity.Normalize(JsonValue.Create(" Short ")));
        Assert.Equal("3", EventIdentity.Normalize(JsonNode.Parse("3")));
    }
}

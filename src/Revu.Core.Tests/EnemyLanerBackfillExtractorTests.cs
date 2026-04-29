using System.Text.Json;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Pure-function tests for the Match-V5 JSON extractors used by
/// <see cref="EnemyLanerBackfillService"/>. Locking these down because
/// (a) they're the equivalent of the LCU champ-select parser for the
/// post-game lane-opponent flow, and (b) Riot has historically reshaped
/// Match-V5 fields between patches without notice.
/// </summary>
public sealed class EnemyLanerBackfillExtractorTests
{
    private const string SelfPuuid = "selfPuuid";

    private static JsonElement BuildMatch(params (string puuid, int teamId, string position, string champ)[] rows)
    {
        var participants = string.Join(",",
            rows.Select(r =>
                $"{{\"puuid\":\"{r.puuid}\",\"teamId\":{r.teamId},\"teamPosition\":\"{r.position}\",\"championName\":\"{r.champ}\"}}"));
        var json = $"{{\"info\":{{\"participants\":[{participants}]}}}}";
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void ExtractEnemyLaner_FindsOppositeTeamSamePosition()
    {
        var match = BuildMatch(
            (SelfPuuid, 100, "MIDDLE", "Ahri"),
            ("a", 100, "TOP", "Garen"),
            ("b", 200, "MIDDLE", "Zed"),
            ("c", 200, "TOP", "Darius"));

        var result = EnemyLanerBackfillService.ExtractEnemyLaner(match, SelfPuuid);

        Assert.Equal("Zed", result);
    }

    [Fact]
    public void ExtractEnemyLaner_ReturnsEmpty_WhenSelfPuuidMissing()
    {
        var match = BuildMatch(
            ("other1", 100, "MIDDLE", "Ahri"),
            ("other2", 200, "MIDDLE", "Zed"));

        var result = EnemyLanerBackfillService.ExtractEnemyLaner(match, SelfPuuid);

        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractEnemyLaner_ReturnsEmpty_WhenAram_NoTeamPosition()
    {
        // ARAM: Match-V5 returns empty teamPosition strings.
        var match = BuildMatch(
            (SelfPuuid, 100, "", "Ahri"),
            ("a", 200, "", "Zed"));

        var result = EnemyLanerBackfillService.ExtractEnemyLaner(match, SelfPuuid);

        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractParticipantMap_BuildsOwnVsEnemyKeysFromSelfTeamPerspective()
    {
        var match = BuildMatch(
            (SelfPuuid, 100, "MIDDLE", "Ahri"),
            ("ally1", 100, "TOP", "Garen"),
            ("ally2", 100, "JUNGLE", "LeeSin"),
            ("ally3", 100, "BOTTOM", "Kaisa"),
            ("ally4", 100, "UTILITY", "Nautilus"),
            ("enemy1", 200, "MIDDLE", "Zed"),
            ("enemy2", 200, "TOP", "Darius"),
            ("enemy3", 200, "JUNGLE", "Graves"),
            ("enemy4", 200, "BOTTOM", "Tristana"),
            ("enemy5", 200, "UTILITY", "Renata"));

        var json = EnemyLanerBackfillService.ExtractParticipantMap(match, SelfPuuid);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

        Assert.Equal("Ahri", map["ownMid"]);
        Assert.Equal("Garen", map["ownTop"]);
        Assert.Equal("LeeSin", map["ownJg"]);
        Assert.Equal("Kaisa", map["ownBot"]);
        Assert.Equal("Nautilus", map["ownSupp"]);
        Assert.Equal("Zed", map["enemyMid"]);
        Assert.Equal("Darius", map["enemyTop"]);
        Assert.Equal("Graves", map["enemyJg"]);
        Assert.Equal("Tristana", map["enemyBot"]);
        Assert.Equal("Renata", map["enemySupp"]);
    }

    [Fact]
    public void ExtractParticipantMap_ReturnsEmpty_WhenAram_NoPositions()
    {
        var match = BuildMatch(
            (SelfPuuid, 100, "", "Ahri"),
            ("a", 100, "", "Garen"),
            ("b", 200, "", "Zed"));

        var result = EnemyLanerBackfillService.ExtractParticipantMap(match, SelfPuuid);

        Assert.Equal("", result);
    }
}

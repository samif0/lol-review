using System.Text.Json;
using Revu.Core.Lcu;

namespace Revu.Core.Tests;

/// <summary>
/// Tests for <see cref="GameEndCaptureService.ResolveEnemyJunglerNames"/> — the EOG
/// role→summoner bridge that tells the jungle-gank classifier who the enemy jungler is.
/// </summary>
public sealed class EnemyJunglerResolutionTests
{
    // myTeamId = 100 throughout; the enemy team is 200.
    private static JsonElement Eog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void UsesExplicitSelectedPosition_ForEnemyTeam()
    {
        var eog = Eog(
            """
            {
              "teams": [
                { "teamId": 100, "players": [
                  { "summonerName": "MeJg", "championName": "Elise", "selectedPosition": "JUNGLE" }
                ]},
                { "teamId": 200, "players": [
                  { "summonerName": "EnemyTop", "championName": "Darius", "selectedPosition": "TOP" },
                  { "summonerName": "EnemyJg",  "championName": "LeeSin", "selectedPosition": "JUNGLE" },
                  { "summonerName": "EnemyMid", "championName": "Ahri",   "selectedPosition": "MIDDLE" }
                ]}
              ]
            }
            """);

        var names = GameEndCaptureService.ResolveEnemyJunglerNames(eog, myTeamId: 100);

        // Only the ENEMY jungler — not my own jungler, not the enemy laners.
        Assert.Equal(new[] { "EnemyJg" }, names.ToArray());
    }

    [Fact]
    public void InfersJungler_FromChampionComp_WhenPositionsBlank()
    {
        // No selectedPosition anywhere ⇒ fall back to champion-role inference. Of these
        // five, Graves is the unambiguous jungle pick, so its summoner is the jungler.
        var eog = Eog(
            """
            {
              "teams": [
                { "teamId": 100, "players": [
                  { "summonerName": "MeMid", "championName": "Syndra", "selectedPosition": "" }
                ]},
                { "teamId": 200, "players": [
                  { "summonerName": "E1", "championName": "Garen",   "selectedPosition": "" },
                  { "summonerName": "E2", "championName": "Graves",  "selectedPosition": "" },
                  { "summonerName": "E3", "championName": "Orianna", "selectedPosition": "" },
                  { "summonerName": "E4", "championName": "Jinx",    "selectedPosition": "" },
                  { "summonerName": "E5", "championName": "Thresh",  "selectedPosition": "" }
                ]}
              ]
            }
            """);

        var names = GameEndCaptureService.ResolveEnemyJunglerNames(eog, myTeamId: 100);

        Assert.Equal(new[] { "E2" }, names.ToArray());
    }

    [Fact]
    public void IncludesRiotIdGameName_NotJustLegacySummonerName()
    {
        // The kill-feed uses the Riot-ID game name, but EOG also carries a stale legacy
        // summonerName. The resolver must surface the Riot-ID game name (and the bare
        // game-name half of a tagged riotId) so the classifier can match the kill-feed.
        var eog = Eog(
            """
            {
              "teams": [
                { "teamId": 100, "players": [ { "summonerName": "MeJg", "championName": "Elise", "selectedPosition": "JUNGLE" } ]},
                { "teamId": 200, "players": [
                  { "summonerName": "oldJgName", "riotIdGameName": "FreshJg", "riotId": "FreshJg#NA1",
                    "championName": "LeeSin", "selectedPosition": "JUNGLE" }
                ]}
              ]
            }
            """);

        var names = GameEndCaptureService.ResolveEnemyJunglerNames(eog, myTeamId: 100);

        // The Riot-ID game name (kill-feed format) MUST be present; the legacy name too.
        Assert.Contains("FreshJg", names);
        Assert.Contains("oldJgName", names);
    }

    [Fact]
    public void ReturnsEmpty_WhenNoEnemyTeam()
    {
        var eog = Eog("""{ "teams": [ { "teamId": 100, "players": [] } ] }""");
        var names = GameEndCaptureService.ResolveEnemyJunglerNames(eog, myTeamId: 100);
        Assert.Empty(names);
    }

    [Fact]
    public void ReturnsEmpty_WhenNoTeamsKey()
    {
        var eog = Eog("""{ "gameId": 123 }""");
        var names = GameEndCaptureService.ResolveEnemyJunglerNames(eog, myTeamId: 100);
        Assert.Empty(names);
    }
}

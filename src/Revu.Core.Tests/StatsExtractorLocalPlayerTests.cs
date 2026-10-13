using System.Text.Json;
using Revu.Core.Lcu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

/// <summary>
/// Match-history extraction never guesses the local player: with several participants
/// and nothing that identifies the player, it returns null instead of recording the
/// first participant's champion, KDA and result as the player's game.
/// </summary>
public sealed class StatsExtractorLocalPlayerTests
{
    [Fact]
    public void SeveralParticipants_NoneIdentified_ReturnsNull()
    {
        var game = Payload(currentPlayerId: null);

        Assert.Null(StatsExtractor.ExtractFromMatchHistory(game, NullLogger.Instance));
    }

    [Fact]
    public void SeveralParticipants_CurrentPlayerFlagged_UsesThatPlayer()
    {
        var stats = StatsExtractor.ExtractFromMatchHistory(Payload(currentPlayerId: 2), NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal("Ahri", stats!.ChampionName);
        Assert.False(stats.Win);
    }

    [Fact]
    public void SeveralParticipants_PreferredParticipant_UsesThatPlayer()
    {
        var stats = StatsExtractor.ExtractFromMatchHistory(Payload(currentPlayerId: null), NullLogger.Instance,
            preferredParticipantId: 2);

        Assert.NotNull(stats);
        Assert.Equal("Ahri", stats!.ChampionName);
    }

    private static JsonElement Payload(int? currentPlayerId)
    {
        string Identity(int id) =>
            $$$"""{"participantId":{{{id}}},"player":{"currentPlayer":{{{(id == currentPlayerId ? "true" : "false")}}}}}""";
        var json = $$$"""
            {
              "gameId": 5531387300, "gameCreation": 1710000000000, "gameDuration": 1800,
              "gameMode": "CLASSIC", "gameType": "MATCHED_GAME", "queueId": 420,
              "participantIdentities": [{{{Identity(1)}}}, {{{Identity(2)}}}],
              "participants": [
                {"participantId":1,"teamId":100,"championId":157,"championName":"Yasuo","stats":{"win":true,"kills":9,"deaths":1,"assists":4}},
                {"participantId":2,"teamId":200,"championId":103,"championName":"Ahri","stats":{"win":false,"kills":2,"deaths":6,"assists":3}}
              ]
            }
            """;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

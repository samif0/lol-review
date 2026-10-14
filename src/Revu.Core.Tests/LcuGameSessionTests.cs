using System.Text.Json;
using Revu.Core.Lcu;

namespace Revu.Core.Tests;

public sealed class LcuGameSessionTests
{
    [Theory]
    [InlineData("{\"gameData\":{\"gameId\":9876,\"queue\":{\"id\":420}}}", 9876, 420)]
    [InlineData("{\"gameData\":{\"gameId\":9876,\"queue\":{\"id\":440}}}", 9876, 440)]
    [InlineData("{\"gameData\":{\"gameId\":9876}}", 9876, null)]
    [InlineData("{\"gameData\":{\"gameId\":9876,\"queue\":{\"id\":-1}}}", 9876, null)]
    [InlineData("{\"gameData\":{\"gameId\":9876,\"queue\":{\"id\":\"420\"}}}", 9876, null)]
    [InlineData("{\"gameData\":{\"gameId\":0,\"queue\":{\"id\":420}}}", 0, null)]
    [InlineData("{\"gameData\":{\"queue\":{\"id\":420}}}", 0, null)]
    [InlineData("{\"gameData\":{\"gameId\":\"9876\",\"queue\":{\"id\":420}}}", 0, null)]
    [InlineData("{\"gameData\":null}", 0, null)]
    [InlineData("[]", 0, null)]
    public void RecordingQueueIsOnlyAcceptedWithCurrentGameIdentity(string json, long gameId, int? queueId)
    {
        var context = LcuClient.ParseCurrentGameSession(JsonSerializer.Deserialize<JsonElement>(json));
        Assert.Equal(gameId, context.GameId);
        Assert.Equal(queueId, context.QueueId);
    }

    [Fact]
    public void MissingSessionDoesNotReuseAnIdentity() => Assert.Equal(new(0, null), LcuClient.ParseCurrentGameSession(null));
}

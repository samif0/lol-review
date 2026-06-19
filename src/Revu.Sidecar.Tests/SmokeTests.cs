using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Proves the SidecarWriteScope harness compiles, builds the full write-path
/// object graph, seeds the schema, and round-trips a seed game. This is the
/// foundation the contract test suite (P1a) builds on; it is NOT a contract
/// test itself.
/// </summary>
public sealed class SmokeTests
{
    [Fact]
    public async Task Harness_InitializesSchema_AndSeedsGame()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        // A repository read against a freshly-initialized DB must succeed and
        // come back empty (schema present, no rows yet).
        var before = await scope.Games.GetRecentCountAsync();
        Assert.Equal(0, before);

        // Seed a game and confirm it round-trips through the games table.
        var seeded = await scope.SeedGameAsync(gameId: 1001);
        var loaded = await scope.Games.GetAsync(seeded.GameId);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.GameId, loaded!.GameId);
        Assert.Equal("Ahri", loaded.ChampionName);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Revu.Sidecar;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The pre-game and in-game matchup card titles itself from the snapshot's
/// HeaderText (pregame.js replaces the static title with it), so the sentence
/// case "Matchup notes vs Enemy" wording has to come from the builder behind
/// GET /api/pregame, not only from the page markup.
/// </summary>
public sealed class PreGameMatchupHistoryTests
{
    [Fact]
    public async Task SavedNotes_HeaderReadsMatchupNotesVsEnemy()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.MatchupNotes.CreateAsync("Jinx", "Caitlyn", "Respect her trap zone before 6");

        var dto = await BuildPreGameAsync(scope, "Jinx", "Caitlyn");

        Assert.True(dto.MatchupHistory.Has);
        Assert.Equal("Matchup notes vs Caitlyn", dto.MatchupHistory.HeaderText);
        Assert.Single(dto.MatchupHistory.Items);
    }

    [Fact]
    public async Task NoSavedNotes_HidesTheCardWithNoHeader()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var dto = await BuildPreGameAsync(scope, "Jinx", "Caitlyn");

        Assert.False(dto.MatchupHistory.Has);
        Assert.Equal("", dto.MatchupHistory.HeaderText);
    }

    // The builder is the seam behind GET /api/pregame (no HTTP host in tests,
    // house style). The intel deck's champion lookup is stubbed out.
    private static Task<PreGameDto> BuildPreGameAsync(SidecarWriteScope scope, string myChampion, string enemy)
    {
        var intel = new PreGameIntelService(
            scope.Objectives,
            scope.SessionLog,
            scope.Games,
            scope.MatchupNotes,
            scope.Prompts,
            new NoChampionData(),
            NullLogger<PreGameIntelService>.Instance);
        var builder = new PreGameSnapshotBuilder(
            scope.Games,
            scope.Objectives,
            scope.SessionLog,
            scope.MatchupNotes,
            scope.Prompts,
            new TiltCheckRepository(scope.ConnectionFactory),
            scope.Config,
            intel,
            new LcuLiveState(),
            NullLogger<PreGameSnapshotBuilder>.Instance);
        return builder.BuildAsync(myChampion, enemy, "bottom", null);
    }

    private sealed class NoChampionData : IRiotChampionDataClient
    {
        public Task<int> ResolveChampionIdAsync(string displayNameOrAlias, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<ChampionAbilities?> GetChampionAbilitiesAsync(int championId, CancellationToken ct = default)
            => Task.FromResult<ChampionAbilities?>(null);
    }
}

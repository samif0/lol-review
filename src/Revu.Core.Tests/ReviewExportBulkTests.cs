using Microsoft.Data.Sqlite;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// "Export all reviews" used to run six queries per game (up to 10,000 games) and send the
/// whole Markdown back through the renderer. It now reads each table once and streams the
/// file straight into a folder.
/// </summary>
public sealed class ReviewExportBulkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "export-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class CountingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public int Opened { get; private set; }
        public string DatabasePath => inner.DatabasePath;
        public SqliteConnection CreateConnection() { Opened++; return inner.CreateConnection(); }
    }

    private static ReviewExportService Service(IDbConnectionFactory factory) =>
        new(new GameRepository(factory, new NoopBackupService()),
            new ObjectivesRepository(factory),
            new ConceptTagRepository(factory),
            new PromptsRepository(factory),
            new VodRepository(factory),
            new MatchupNotesRepository(factory),
            new EvidenceRepository(factory),
            new SessionLogRepository(factory),
            new TestConfigService());

    // Every per-game section populated, so each one would have cost its own query.
    private static async Task SeedAsync(TestDatabaseScope scope, int games)
    {
        var objectiveId = await scope.Objectives.CreateAsync("Crash before recall", completionCriteria: "Wave hits tower");
        var promptId = await scope.Prompts.CreatePromptAsync(objectiveId, ObjectivePhases.PostGame, "Did you crash?", 0);
        var tagId = await scope.ConceptTags.CreateAsync("reset timing", "negative", "#D38C90");
        for (var i = 0; i < games; i++)
        {
            long gameId = 30_000 + i;
            await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: $"Champ{i}", timestamp: 1_700_000_000 + i));
            await scope.Objectives.RecordGameAsync(gameId, objectiveId, practiced: true, executionNote: $"note {gameId}");
            await scope.Prompts.SaveAnswerAsync(promptId, gameId, $"answer {gameId}");
            await scope.ConceptTags.SetForGameAsync(gameId, [tagId]);
            await scope.MatchupNotes.UpsertForGameAsync(gameId, $"Champ{i}", "Zed", $"matchup {gameId}");
            await scope.Vod.LinkVodAsync(gameId, $@"C:\vods\{gameId}.mp4", 1, 1800);
            await scope.Vod.AddBookmarkAsync(gameId, 600, $"bookmark {gameId}");
        }
    }

    [Fact]
    public async Task TheQueryCount_DoesNotGrowWithHistory()
    {
        async Task<int> OpenedForAsync(int games)
        {
            using var scope = new TestDatabaseScope();
            await scope.InitializeAsync();
            await SeedAsync(scope, games);
            var counting = new CountingFactory(scope.ConnectionFactory);
            var markdown = await Service(counting).ExportAllAsync();
            Assert.Contains($"Games exported:** {games}", markdown);
            return counting.Opened;
        }

        var few = await OpenedForAsync(2);
        var many = await OpenedForAsync(40);

        Assert.Equal(few, many);
        Assert.True(many <= 10, $"export opened {many} connections");
    }

    [Fact]
    public async Task EachGameKeepsItsOwnSections()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedAsync(scope, 3);

        var markdown = await Service(scope.ConnectionFactory).ExportAllAsync();

        // Newest first; every section lands under its own game's heading.
        var sections = markdown.Split("\n## ").Skip(1).ToArray();
        Assert.Equal(3, sections.Length);
        for (var i = 0; i < 3; i++)
        {
            var section = sections[2 - i];
            var gameId = 30_000 + i;
            Assert.Contains($"Champ{i}", section);
            Assert.Contains($"note {gameId}", section);
            Assert.Contains($"answer {gameId}", section);
            Assert.Contains($"matchup {gameId}", section);
            Assert.Contains($@"C:\vods\{gameId}.mp4", section);
            Assert.Contains($"bookmark {gameId}", section);
            Assert.Contains("reset timing", section);
            Assert.DoesNotContain($"note {gameId + 1}", section);
        }
    }

    [Fact]
    public async Task ExportToFolder_SavesTheSameMarkdown_AndNeverReplacesAnEarlierExport()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedAsync(scope, 2);
        var service = Service(scope.ConnectionFactory);

        var first = await service.ExportAllToFolderAsync(_folder);
        var second = await service.ExportAllToFolderAsync(_folder);

        Assert.Equal(2, first.GameCount);
        Assert.Equal(_folder, Path.GetDirectoryName(first.Path));
        Assert.Matches(@"^revu-review-export-\d{8}-\d{4}(-\d+)?\.md$", first.FileName);
        Assert.NotEqual(first.Path, second.Path);
        Assert.True(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
        static string WithoutStamp(string text) =>
            string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Where(line => !line.StartsWith("- **Exported:**")));
        Assert.Equal(WithoutStamp(await service.ExportAllAsync()), WithoutStamp(await File.ReadAllTextAsync(first.Path)));
        // No byte-order mark, and no temporary files left behind.
        Assert.NotEqual(0xEF, (await File.ReadAllBytesAsync(first.Path))[0]);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public async Task ACancelledExport_LeavesNothingBehind()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedAsync(scope, 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(scope.ConnectionFactory).ExportAllToFolderAsync(_folder, new CancellationToken(canceled: true)));

        Assert.Empty(Directory.GetFiles(_folder));
    }
}

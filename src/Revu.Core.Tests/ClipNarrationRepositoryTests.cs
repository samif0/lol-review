using Microsoft.Data.Sqlite;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>clip_narrations persistence: upsert/generation semantics, the transcript
/// compare-and-set, startup recovery, orphan sweep, protected paths, missing-table tolerance.</summary>
public sealed class ClipNarrationRepositoryTests
{
    internal static ClipNarrationRecord Record(long bookmarkId, long gameId, string audio = "a.webm",
        string narrated = "", string source = "", int offsetMs = 0, string status = TranscriptStatuses.Pending,
        int durationMs = 30_000) =>
        new(bookmarkId, gameId, Guid.NewGuid().ToString("D"), audio, narrated, source, offsetMs, durationMs,
            0.8, 1.0, true, status, 0, "", "", "", "", 0, 0);

    private static async Task<(TestDatabaseScope Scope, ClipNarrationRepository Repo, long GameId, long BookmarkId)> SetupAsync()
    {
        var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var bm = await scope.Vod.AddBookmarkAsync(gameId, 100, "clip", clipStartSeconds: 90, clipEndSeconds: 130,
            clipPath: @"C:\clips\a.mp4");
        return (scope, new ClipNarrationRepository(scope.ConnectionFactory), gameId, bm);
    }

    [Fact]
    public async Task Upsert_ReturnsPreviousRecord_BumpsGeneration_KeepsCreatedAt_AndClearsTranscript()
    {
        var (scope, repo, gameId, bm) = await SetupAsync();
        using (scope)
        {
            Assert.Null(await repo.UpsertAsync(Record(bm, gameId, audio: "first.webm", narrated: "first.mp4")));
            var first = (await repo.GetAsync(bm))!;
            Assert.Equal("first.webm", first.AudioPath);
            Assert.Equal(TranscriptStatuses.Pending, first.TranscriptStatus);

            // A finished transcript on the first take.
            Assert.True(await repo.TryClaimTranscriptAsync(bm, first.TranscriptGeneration));
            Assert.True(await repo.TrySetTranscriptAsync(bm, first.TranscriptGeneration, TranscriptStatuses.Ready, "en", "{}", ""));
            await repo.SetTranscriptPushedSlugAsync(bm, "abc1234");

            var previous = await repo.UpsertAsync(Record(bm, gameId, audio: "second.webm", narrated: "second.mp4",
                status: TranscriptStatuses.NeedsLogin));

            Assert.NotNull(previous);
            Assert.Equal("first.webm", previous!.AudioPath);
            Assert.Equal("{}", previous.TranscriptJson);
            var second = (await repo.GetAsync(bm))!;
            Assert.Equal("second.webm", second.AudioPath);
            Assert.Equal("second.mp4", second.NarratedClipPath);
            Assert.Equal(first.TranscriptGeneration + 1, second.TranscriptGeneration);
            Assert.Equal(first.CreatedAt, second.CreatedAt);
            Assert.Equal(TranscriptStatuses.NeedsLogin, second.TranscriptStatus);
            Assert.Equal("", second.TranscriptJson);
            Assert.Equal("", second.TranscriptLanguage);
            Assert.Equal("", second.TranscriptPushedSlug);
        }
    }

    [Fact]
    public async Task UpdateMix_WithoutReset_KeepsTranscript_WithReset_BumpsAndClears()
    {
        var (scope, repo, gameId, bm) = await SetupAsync();
        using (scope)
        {
            await repo.UpsertAsync(Record(bm, gameId, narrated: "one.mp4"));
            var row = (await repo.GetAsync(bm))!;
            await repo.TryClaimTranscriptAsync(bm, row.TranscriptGeneration);
            await repo.TrySetTranscriptAsync(bm, row.TranscriptGeneration, TranscriptStatuses.Ready, "en", "{\"x\":1}", "");

            var prev = await repo.UpdateMixAsync(bm, "two.mp4", 0, 1.2, 1.5, false, resetTranscript: false, TranscriptStatuses.Pending);
            Assert.Equal("one.mp4", prev!.NarratedClipPath);
            var kept = (await repo.GetAsync(bm))!;
            Assert.Equal("two.mp4", kept.NarratedClipPath);
            Assert.Equal(1.2, kept.GameVolume);
            Assert.Equal(1.5, kept.NarrationVolume);
            Assert.False(kept.Duck);
            Assert.Equal(row.TranscriptGeneration, kept.TranscriptGeneration);
            Assert.Equal(TranscriptStatuses.Ready, kept.TranscriptStatus);
            Assert.Equal("{\"x\":1}", kept.TranscriptJson);

            await repo.UpdateMixAsync(bm, "three.mp4", 250, 0.8, 1.0, true, resetTranscript: true, TranscriptStatuses.NeedsLogin);
            var reset = (await repo.GetAsync(bm))!;
            Assert.Equal(250, reset.OffsetMs);
            Assert.Equal(row.TranscriptGeneration + 1, reset.TranscriptGeneration);
            Assert.Equal(TranscriptStatuses.NeedsLogin, reset.TranscriptStatus);
            Assert.Equal("", reset.TranscriptJson);

            Assert.Null(await repo.UpdateMixAsync(999_999, "x.mp4", 0, 1, 1, true, true, TranscriptStatuses.Pending));
        }
    }

    [Fact]
    public async Task ClaimAndSet_AreCompareAndSet_StaleGenerationChangesNothing()
    {
        var (scope, repo, gameId, bm) = await SetupAsync();
        using (scope)
        {
            await repo.UpsertAsync(Record(bm, gameId));
            var gen = (await repo.GetAsync(bm))!.TranscriptGeneration;

            Assert.False(await repo.TryClaimTranscriptAsync(bm, gen + 7));   // wrong generation
            Assert.True(await repo.TryClaimTranscriptAsync(bm, gen));
            Assert.False(await repo.TryClaimTranscriptAsync(bm, gen));       // already processing
            Assert.Equal(TranscriptStatuses.Processing, (await repo.GetAsync(bm))!.TranscriptStatus);

            // A re-record lands while the run works: the generation moves on.
            Assert.True(await repo.ResetTranscriptAsync(bm, TranscriptStatuses.Pending));
            var before = (await repo.GetAsync(bm))!;
            Assert.Equal(gen + 1, before.TranscriptGeneration);

            Assert.False(await repo.TrySetTranscriptAsync(bm, gen, TranscriptStatuses.Ready, "en", "{\"stale\":true}", ""));
            var after = (await repo.GetAsync(bm))!;
            Assert.Equal(before, after);

            Assert.True(await repo.TryClaimTranscriptAsync(bm, gen + 1));
            Assert.True(await repo.TrySetTranscriptAsync(bm, gen + 1, TranscriptStatuses.Failed, "", "", "Transcript failed. Try again."));
            Assert.Equal("Transcript failed. Try again.", (await repo.GetAsync(bm))!.TranscriptError);
            Assert.False(await repo.ResetTranscriptAsync(424242, TranscriptStatuses.Pending));
        }
    }

    [Fact]
    public async Task ResetProcessingToPending_RecoversInterruptedRuns_AndListsByStatus()
    {
        var (scope, repo, gameId, bm) = await SetupAsync();
        using (scope)
        {
            var bm2 = await scope.Vod.AddBookmarkAsync(gameId, 200, "clip 2", clipPath: @"C:\clips\b.mp4");
            await repo.UpsertAsync(Record(bm, gameId));
            await repo.UpsertAsync(Record(bm2, gameId, status: TranscriptStatuses.NeedsLogin));
            await repo.TryClaimTranscriptAsync(bm, (await repo.GetAsync(bm))!.TranscriptGeneration);

            Assert.Single(await repo.ListByTranscriptStatusAsync(TranscriptStatuses.Processing));
            Assert.Equal(1, await repo.ResetProcessingToPendingAsync());
            Assert.Equal(TranscriptStatuses.Pending, (await repo.GetAsync(bm))!.TranscriptStatus);
            Assert.Equal(2, (await repo.ListByTranscriptStatusAsync(TranscriptStatuses.Pending, TranscriptStatuses.NeedsLogin)).Count);
            Assert.Equal(2, (await repo.GetForGameAsync(gameId)).Count);
            Assert.Equal(new[] { "a.webm", "a.webm" }, await repo.ListAudioPathsAsync());
        }
    }

    [Fact]
    public async Task DeleteOrphans_RemovesRowsWhoseBookmarkIsGone_AndReturnsThem()
    {
        var (scope, repo, gameId, bm) = await SetupAsync();
        using (scope)
        {
            await repo.UpsertAsync(Record(bm, gameId, audio: "kept.webm"));
            // A downgraded build deleted bookmark 777's row but not its narration.
            await repo.UpsertAsync(Record(777_777, gameId, audio: "orphan.webm", narrated: "orphan.mp4"));

            var orphans = await repo.DeleteOrphansAsync();

            Assert.Single(orphans);
            Assert.Equal("orphan.mp4", orphans[0].NarratedClipPath);
            Assert.Null(await repo.GetAsync(777_777));
            Assert.NotNull(await repo.GetAsync(bm));
            Assert.Empty(await repo.DeleteOrphansAsync());

            var deleted = await repo.DeleteAsync(bm);
            Assert.Equal("kept.webm", deleted!.AudioPath);
            Assert.Null(await repo.DeleteAsync(bm));
        }
    }

    [Fact]
    public async Task ProtectedClipPaths_AreTheNonEmptySourcesAndRenders()
    {
        var (scope, repo, gameId, bm) = await SetupAsync();
        using (scope)
        {
            var bm2 = await scope.Vod.AddBookmarkAsync(gameId, 200, "clip 2", clipPath: @"C:\clips\b.mp4");
            await repo.UpsertAsync(Record(bm, gameId, narrated: @"C:\clips\narrated\a_n.mp4", source: @"C:\clips\a.mp4"));
            await repo.UpsertAsync(Record(bm2, gameId, narrated: "", source: @"C:\clips\b.mp4"));

            var paths = await repo.ListProtectedClipPathsAsync();

            Assert.Equal(
                new[] { @"C:\clips\a.mp4", @"C:\clips\b.mp4", @"C:\clips\narrated\a_n.mp4" },
                paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public async Task MissingTable_ReadsAreEmpty_AndAWriteCreatesTheTable()
    {
        var (scope, repo, gameId, bm) = await SetupAsync();
        using (scope)
        {
            using (var conn = scope.OpenConnection())
            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = "DROP TABLE clip_narrations";
                await drop.ExecuteNonQueryAsync();
            }

            Assert.Null(await repo.GetAsync(bm));
            Assert.Empty(await repo.GetForGameAsync(gameId));
            Assert.Empty(await repo.ListProtectedClipPathsAsync());
            Assert.Empty(await repo.ListAudioPathsAsync());
            Assert.Empty(await repo.ListByTranscriptStatusAsync(TranscriptStatuses.Pending));
            // VodRepository delete paths tolerate the missing table too.
            await scope.Vod.DeleteBookmarkAsync(999);
            Assert.False(await repo.TryClaimTranscriptAsync(bm, 1));

            Assert.Null(await repo.UpsertAsync(Record(bm, gameId)));
            Assert.NotNull(await repo.GetAsync(bm));
        }
    }

    [Fact]
    public async Task ReadOnlyFactory_TreatsAMissingTableAsEmpty()
    {
        var (scope, _, gameId, bm) = await SetupAsync();
        using (scope)
        {
            using (var conn = scope.OpenConnection())
            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = "DROP TABLE clip_narrations";
                await drop.ExecuteNonQueryAsync();
            }
            var readOnly = new ClipNarrationRepository(new ReadOnlyFactory(scope.DatabasePath));
            Assert.Empty(await readOnly.GetForGameAsync(gameId));
            Assert.Null(await readOnly.GetAsync(bm));
        }
    }

    private sealed class ReadOnlyFactory(string path) : Revu.Core.Data.IDbConnectionFactory
    {
        public string DatabasePath => path;

        public SqliteConnection CreateConnection()
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            conn.Open();
            return conn;
        }
    }
}

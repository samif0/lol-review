using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class DataSnapshotServiceTests
{
    [Fact]
    public async Task OnlineSnapshot_RestoresWalDataAllIdsCorrectionsAssociationsAndMediaReferences()
    {
        using var fixture = new SnapshotFixture();
        await fixture.InitializeAsync();
        using var owner = DataRootLease.Acquire(fixture.Source);
        using var writer = fixture.OpenSource();
        Execute(writer, "PRAGMA wal_autocheckpoint=0; PRAGMA wal_checkpoint(TRUNCATE)");
        Execute(writer, """
            INSERT INTO games(id,game_id,review_notes) VALUES(41,7001,'Synthetic review — preserve exactly');
            INSERT INTO objectives(id,title) VALUES(51,'Synthetic spacing objective');
            INSERT INTO game_objectives(id,game_id,objective_id,execution_note) VALUES(61,7001,51,'Review association');
            INSERT INTO objective_event_types(objective_id,event_token) VALUES(51,'DEATH');
            INSERT INTO game_events(id,game_id,event_type,game_time_s,details,event_key)
              VALUES(71,7001,'DEATH',812,'{"killer":"Ahri"}','det:DEATH:812:Ahri');
            INSERT INTO vod_files(id,game_id,file_path,file_size,duration_s)
              VALUES(81,7001,'X:\synthetic-external-media\vod.mp4',12345,1800);
            INSERT INTO vod_bookmarks(id,game_id,game_time_s,note,clip_start_s,clip_end_s,clip_path)
              VALUES(91,7001,815,'Synthetic bookmark',805,825,'X:\synthetic-external-media\clip.mp4');
            INSERT INTO evidence_items(id,game_id,source_kind,source_id,source_key,objective_id,note)
              VALUES(101,7001,'bookmark',91,'synthetic-bookmark',51,'Preserve objective evidence');
            -- Fixture-only future metadata demonstrates opaque value preservation;
            -- the current product does not yet have a media/game offset mapping.
            CREATE TABLE harness_media_clock(id INTEGER PRIMARY KEY, game_id INTEGER REFERENCES games(game_id),
              offset_seconds REAL, payload BLOB, optional_note TEXT);
            INSERT INTO harness_media_clock VALUES(111,7001,12.5,X'0001FF',NULL);
            PRAGMA user_version=7;
            """);

        var corrections = new EventCorrectionsRepository(fixture.Factory);
        var correction = await corrections.SaveAsync(new EventCorrectionRequest(7001,
            "00000000-0000-0000-0000-000000000123", CorrectionOps.Retime,
            new EventCorrectionSubject("det:DEATH:812:Ahri", 71, "DEATH", 812),
            new EventPatch(null, 815, null, null), "Synthetic timing correction", "harness", null));
        Assert.Equal("", correction.Message);
        Assert.True(new FileInfo(fixture.DatabasePath + "-wal").Length > 0);

        var service = new DataSnapshotService();
        var before = service.Create(fixture.Source, fixture.Bundle);
        Assert.Equal(Schema.CurrentAppSchemaVersion, before.AppSchemaVersion);
        Assert.Equal(7, before.SqliteUserVersion);
        Assert.Equal("ok", before.IntegrityCheck);
        Assert.Equal(0, before.ForeignKeyViolations);
        Assert.Equal(2, before.MediaReferences.Count);
        Assert.DoesNotContain(Directory.GetFiles(fixture.Bundle), file => file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Hash(Path.Combine(fixture.Bundle, "revu.db")), before.DatabaseSha256);
        Assert.Equal(Hash(Path.Combine(fixture.Source, "config.json")), before.ConfigSha256);
        foreach (var table in new[] { "games", "game_events", "event_corrections", "game_objectives",
                     "objective_event_types", "evidence_items", "vod_bookmarks", "vod_files", "harness_media_clock" })
            Assert.Equal(1, Assert.Single(before.Tables, t => t.Name == table).RowCount);

        // Changes committed after the snapshot stay in the live source only.
        Execute(writer, "UPDATE games SET review_notes='Later live change' WHERE game_id=7001");
        var after = service.Restore(fixture.Bundle, fixture.Restored);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(service.Verify(fixture.Restored)));
        using var restored = fixture.OpenRestored();
        Assert.Equal(41L, Scalar(restored, "SELECT id FROM games WHERE game_id=7001"));
        Assert.Equal("Synthetic review — preserve exactly", Scalar(restored, "SELECT review_notes FROM games"));
        Assert.Equal(71L, Scalar(restored, "SELECT id FROM game_events"));
        Assert.Equal(815L, Scalar(restored, "SELECT game_time_s FROM game_events"));
        Assert.Equal("det:DEATH:812:Ahri", Scalar(restored, "SELECT subject_key FROM event_corrections"));
        Assert.Equal(71L, Scalar(restored, "SELECT applied_event_id FROM event_corrections"));
        Assert.Equal(51L, Scalar(restored, "SELECT objective_id FROM evidence_items"));
        Assert.Equal("DEATH", Scalar(restored, "SELECT event_token FROM objective_event_types"));
        Assert.Equal(805L, Scalar(restored, "SELECT clip_start_s FROM vod_bookmarks"));
        Assert.Equal(12.5, Scalar(restored, "SELECT offset_seconds FROM harness_media_clock"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Source, "config.json")),
            File.ReadAllBytes(Path.Combine(fixture.Restored, "config.json")));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("config")]
    [InlineData("manifest")]
    [InlineData("journal")]
    public async Task TamperedBundle_IsRejectedBeforeRestoreCreatesTarget(string tamper)
    {
        using var fixture = new SnapshotFixture();
        await fixture.InitializeAsync();
        var service = new DataSnapshotService();
        service.Create(fixture.Source, fixture.Bundle);
        switch (tamper)
        {
            case "database":
                using (var connection = SnapshotFixture.Open(Path.Combine(fixture.Bundle, "revu.db"), SqliteOpenMode.ReadWrite))
                    Execute(connection, "INSERT INTO games(game_id) VALUES(7002)");
                break;
            case "config": File.AppendAllText(Path.Combine(fixture.Bundle, "config.json"), " "); break;
            case "manifest":
                var path = Path.Combine(fixture.Bundle, DataSnapshotService.ManifestFileName);
                var manifest = JsonSerializer.Deserialize<DataSnapshotManifest>(File.ReadAllText(path))!;
                File.WriteAllText(path, JsonSerializer.Serialize(manifest with { AppSchemaVersion = 0 }));
                break;
            case "journal": File.WriteAllBytes(Path.Combine(fixture.Bundle, "revu.db-wal"), []); break;
        }
        Assert.Throws<InvalidDataException>(() => service.Restore(fixture.Bundle, fixture.Restored));
        Assert.False(Directory.Exists(fixture.Restored));
    }

    [Fact]
    public async Task NewerSchema_IsPreservedAndRejectedByOlderBackend()
    {
        using var fixture = new SnapshotFixture();
        await fixture.InitializeAsync();
        using (var connection = fixture.OpenSource())
            Execute(connection, $"UPDATE schema_metadata SET value='{Schema.CurrentAppSchemaVersion + 1}' WHERE key='app_schema_version'");
        var original = Hash(fixture.DatabasePath);
        Assert.Throws<InvalidDataException>(() => DatabaseSchemaCompatibility.EnsureCompatible(fixture.DatabasePath));
        Assert.Equal(original, Hash(fixture.DatabasePath));

        var service = new DataSnapshotService();
        service.Create(fixture.Source, fixture.Bundle); // Backing up newer data is safe.
        Assert.Throws<InvalidDataException>(() => service.Restore(fixture.Bundle, fixture.Restored));
        Assert.False(Directory.Exists(fixture.Restored));
        service.Restore(fixture.Bundle, fixture.Restored, Schema.CurrentAppSchemaVersion + 1);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("unknown")]
    [InlineData("9999999999999999")]
    public async Task MalformedSchema_FailsClosed(string version)
    {
        using var fixture = new SnapshotFixture();
        await fixture.InitializeAsync();
        using (var connection = fixture.OpenSource())
            Execute(connection, $"UPDATE schema_metadata SET value='{version}' WHERE key='app_schema_version'");
        Assert.Throws<InvalidDataException>(() => DatabaseSchemaCompatibility.EnsureCompatible(fixture.DatabasePath));
    }

    [Fact]
    public async Task NonemptyDestination_IsNotOverwritten()
    {
        using var fixture = new SnapshotFixture();
        await fixture.InitializeAsync();
        var service = new DataSnapshotService();
        service.Create(fixture.Source, fixture.Bundle);
        Directory.CreateDirectory(fixture.Restored);
        var sentinel = Path.Combine(fixture.Restored, "revu.db");
        File.WriteAllText(sentinel, "Existing data must survive");
        Assert.Throws<IOException>(() => service.Restore(fixture.Bundle, fixture.Restored));
        Assert.Equal("Existing data must survive", File.ReadAllText(sentinel));
        Assert.Throws<IOException>(() => service.Create(fixture.Source, fixture.Bundle));
        service.Verify(fixture.Bundle);
    }

    [WindowsFact]
    public async Task OwnerLease_BlocksAliasesAndRestore_ThenAllowsUseAfterRelease()
    {
        using var fixture = new SnapshotFixture();
        await fixture.InitializeAsync();
        var service = new DataSnapshotService();
        service.Create(fixture.Source, fixture.Bundle);
        Directory.CreateDirectory(fixture.Restored);
        using (var owner = DataRootLease.Acquire(fixture.Restored))
        {
            Assert.Equal(DataRootLease.Canonicalize(fixture.Restored), owner.DataDirectory);
            Assert.Throws<IOException>(() => DataRootLease.Acquire(Path.Combine(fixture.Restored, ".") + Path.DirectorySeparatorChar));
            Assert.Throws<IOException>(() => service.Restore(fixture.Bundle, fixture.Restored));
        }
        service.Restore(fixture.Bundle, fixture.Restored);
        using var nextOwner = DataRootLease.Acquire(fixture.Restored);
    }

    [Fact]
    public async Task ForeignKeyViolation_AbortsSnapshotAndLeavesSourceUntouched()
    {
        using var fixture = new SnapshotFixture();
        await fixture.InitializeAsync();
        using (var connection = fixture.OpenSource())
            Execute(connection, "PRAGMA foreign_keys=OFF; INSERT INTO game_events(game_id,event_type,game_time_s) VALUES(9999,'DEATH',12)");
        var service = new DataSnapshotService();
        Assert.Throws<InvalidDataException>(() => service.Create(fixture.Source, fixture.Bundle));
        Assert.False(File.Exists(Path.Combine(fixture.Bundle, "revu.db")));
        using var source = fixture.OpenSource();
        Assert.Equal(1L, Scalar(source, "SELECT COUNT(*) FROM game_events"));
    }

    [Fact]
    public async Task MissingConfiguration_IsRecordedAndRestoredAsMissing()
    {
        using var fixture = new SnapshotFixture();
        await fixture.InitializeAsync();
        File.Delete(Path.Combine(fixture.Source, "config.json"));
        var service = new DataSnapshotService();
        Assert.Null(service.Create(fixture.Source, fixture.Bundle).ConfigSha256);
        service.Restore(fixture.Bundle, fixture.Restored);
        Assert.False(File.Exists(Path.Combine(fixture.Restored, "config.json")));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class SnapshotFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.Snapshot.Tests", Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_root, "source");
        public string Bundle => Path.Combine(_root, "bundle");
        public string Restored => Path.Combine(_root, "restored");
        public string DatabasePath => Path.Combine(Source, "revu.db");
        public IDbConnectionFactory Factory { get; }

        public SnapshotFixture()
        {
            Directory.CreateDirectory(Source);
            Factory = new FixtureConnectionFactory(DatabasePath);
            File.WriteAllText(Path.Combine(Source, "config.json"), "{\"fixture\":true,\"AscentFolder\":\"X:\\\\synthetic-external-media\"}");
        }

        public Task InitializeAsync() => new DatabaseInitializer(Factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        public SqliteConnection OpenSource() => Open(DatabasePath, SqliteOpenMode.ReadWrite);
        public SqliteConnection OpenRestored() => Open(Path.Combine(Restored, "revu.db"), SqliteOpenMode.ReadOnly);
        public static SqliteConnection Open(string path, SqliteOpenMode mode)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = mode, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);

        private sealed class FixtureConnectionFactory(string path) : IDbConnectionFactory
        {
            public string DatabasePath => path;
            public SqliteConnection CreateConnection() => Open(path, SqliteOpenMode.ReadWriteCreate);
        }
    }
}

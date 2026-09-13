using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Revu.Core.Data;

/// <summary>
/// Explicit-path compatibility harness. Takes a consistent SQLite online backup,
/// copies config separately, and verifies every table before restoring to an empty
/// directory. It never opens referenced videos/clips or replaces an existing database.
/// </summary>
public sealed class DataSnapshotService
{
    public const string DatabaseFileName = "revu.db";
    public const string ConfigFileName = "config.json";
    public const string ManifestFileName = "snapshot-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DataSnapshotManifest Create(string sourceDataDirectory, string bundleDirectory)
    {
        var source = DataRootLease.Canonicalize(sourceDataDirectory);
        var sourceDb = Path.Combine(source, DatabaseFileName);
        if (!File.Exists(sourceDb)) throw new FileNotFoundException("Source database is missing.", sourceDb);
        using var lease = AcquireEmptyDirectory(bundleDirectory);
        var destination = lease.DataDirectory;
        var db = Path.Combine(destination, DatabaseFileName);
        var config = Path.Combine(destination, ConfigFileName);
        var manifestPath = Path.Combine(destination, ManifestFileName);

        // All paths are in the new, exclusively owned directory. On failure only
        // files created by this operation are removed; the source is never changed.
        try
        {
            using (var sourceConnection = Open(sourceDb, SqliteOpenMode.ReadOnly))
            using (var targetConnection = Open(db, SqliteOpenMode.ReadWriteCreate))
            {
                sourceConnection.BackupDatabase(targetConnection);
                using var journal = targetConnection.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE";
                if (!string.Equals(Convert.ToString(journal.ExecuteScalar()), "delete", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Could not finalize a self-contained database snapshot.");
            }

            var sourceConfig = Path.Combine(source, ConfigFileName);
            if (File.Exists(sourceConfig)) CopyNewFile(sourceConfig, config);
            var manifest = CaptureManifest(db, File.Exists(config) ? config : null);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
            return manifest;
        }
        catch
        {
            RemoveCreatedFiles(destination);
            throw;
        }
    }

    public DataSnapshotManifest Verify(string bundleDirectory)
    {
        var bundle = DataRootLease.Canonicalize(bundleDirectory);
        var manifest = JsonSerializer.Deserialize<DataSnapshotManifest>(File.ReadAllText(Path.Combine(bundle, ManifestFileName)))
            ?? throw new InvalidDataException("Snapshot manifest is missing or invalid.");
        if (manifest.FormatVersion != 1) throw new InvalidDataException("Unsupported snapshot manifest format.");

        var db = Path.Combine(bundle, DatabaseFileName);
        RejectJournals(db);
        var config = Path.Combine(bundle, ConfigFileName);
        if (File.Exists(config) != (manifest.ConfigSha256 is not null))
            throw new InvalidDataException("Configuration presence does not match the snapshot manifest.");
        var actual = CaptureManifest(db, File.Exists(config) ? config : null) with { CreatedAtUtc = manifest.CreatedAtUtc };
        if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(manifest))
            throw new InvalidDataException("Snapshot hashes or database invariants do not match the manifest.");
        return manifest;
    }

    /// <summary>
    /// Restore only to an empty directory with no owner. An older binary never
    /// restores over newer data. Media paths retain their original values; media
    /// bytes and external output directories are not included in this bundle.
    /// </summary>
    public DataSnapshotManifest Restore(string bundleDirectory, string targetDataDirectory,
        int maximumSchemaVersion = Schema.CurrentAppSchemaVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSchemaVersion);
        var bundle = DataRootLease.Canonicalize(bundleDirectory);
        var manifest = Verify(bundle);
        if (manifest.AppSchemaVersion > maximumSchemaVersion)
            throw new InvalidDataException($"Snapshot schema {manifest.AppSchemaVersion} exceeds backend schema {maximumSchemaVersion}.");

        using var lease = AcquireEmptyDirectory(targetDataDirectory);
        var target = lease.DataDirectory;
        try
        {
            // Verification is repeated on the destination after copying, so a
            // changed bundle cannot silently pass the earlier preflight checks.
            CopyNewFile(Path.Combine(bundle, DatabaseFileName), Path.Combine(target, DatabaseFileName));
            if (manifest.ConfigSha256 is not null)
                CopyNewFile(Path.Combine(bundle, ConfigFileName), Path.Combine(target, ConfigFileName));
            File.WriteAllText(Path.Combine(target, ManifestFileName), JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
            Verify(target);
            return manifest;
        }
        catch
        {
            RemoveCreatedFiles(target);
            throw;
        }
    }

    private static DataRootLease AcquireEmptyDirectory(string directory)
    {
        var canonical = DataRootLease.Canonicalize(directory);
        Directory.CreateDirectory(canonical);
        var lease = DataRootLease.Acquire(canonical);
        try
        {
            if (Directory.EnumerateFileSystemEntries(canonical)
                .Any(entry => !string.Equals(Path.GetFileName(entry), DataRootLease.FileName, StringComparison.Ordinal)))
                throw new IOException("Snapshot and restore destinations must be empty; existing data is never overwritten.");
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static DataSnapshotManifest CaptureManifest(string databasePath, string? configPath)
    {
        RejectJournals(databasePath);
        List<SnapshotTable> tables = [];
        List<SnapshotMediaReference> media = [];
        int schemaVersion;
        long userVersion;
        string schemaHash;
        using (var connection = Open(databasePath, SqliteOpenMode.ReadOnly))
        {
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            using (var reader = integrity.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                    throw new InvalidDataException("Snapshot failed SQLite integrity_check.");
            }
            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using (var reader = foreignKeys.ExecuteReader())
            {
                if (reader.Read()) throw new InvalidDataException("Snapshot contains foreign-key violations.");
            }

            schemaVersion = DatabaseSchemaCompatibility.ReadVersion(connection);
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            userVersion = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);

            var names = new List<string>();
            var definitions = new List<string>();
            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name";
            using (var reader = schema.ExecuteReader())
            {
                while (reader.Read())
                {
                    definitions.Add(HashRow(reader, Enumerable.Range(0, reader.FieldCount)));
                    if (reader.GetString(0) == "table") names.Add(reader.GetString(1));
                }
            }
            schemaHash = HashSorted(definitions);
            foreach (var name in names.Order(StringComparer.Ordinal))
                tables.Add(CaptureTable(connection, name, media));
        }

        return new DataSnapshotManifest(1, DateTimeOffset.UtcNow, schemaVersion, userVersion,
            FileHash(databasePath), configPath is null ? null : FileHash(configPath), schemaHash,
            "ok", 0, tables, media);
    }

    private static SnapshotTable CaptureTable(SqliteConnection connection, string table, List<SnapshotMediaReference> media)
    {
        var columns = new List<SnapshotColumn>();
        using var metadata = connection.CreateCommand();
        metadata.CommandText = $"PRAGMA table_xinfo({Quote(table)})";
        using (var reader = metadata.ExecuteReader())
        {
            while (reader.Read())
            {
                // Hidden virtual-table columns are not returned by SELECT *.
                if (reader.GetInt32(6) != 1)
                    columns.Add(new SnapshotColumn(reader.GetString(1), reader.GetString(2), reader.GetInt32(5)));
            }
        }

        var keys = columns.Select((column, index) => (column, index))
            .Where(item => item.column.PrimaryKeyOrder > 0)
            .OrderBy(item => item.column.PrimaryKeyOrder).Select(item => item.index).ToArray();
        var pathColumns = columns.Select((column, index) => (column, index))
            .Where(item => item.column.Name is "file_path" or "clip_path").ToArray();
        var rows = new List<string>();
        var identities = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(table)}";
        using var data = command.ExecuteReader();
        while (data.Read())
        {
            var rowHash = HashRow(data, Enumerable.Range(0, data.FieldCount));
            var keyHash = keys.Length == 0 ? rowHash : HashRow(data, keys);
            rows.Add(rowHash);
            identities.Add(keyHash);
            foreach (var (column, index) in pathColumns)
            {
                if (!data.IsDBNull(index) && data.GetString(index).Length > 0)
                    media.Add(new SnapshotMediaReference(table, keyHash, column.Name, data.GetString(index)));
            }
        }
        media.Sort((left, right) => StringComparer.Ordinal.Compare(
            JsonSerializer.Serialize(left), JsonSerializer.Serialize(right)));
        return new SnapshotTable(table, rows.Count, columns, HashSorted(identities), HashSorted(rows));
    }

    private static string HashRow(SqliteDataReader reader, IEnumerable<int> indexes)
    {
        // Type tags + length framing preserve NULL versus empty, INTEGER versus
        // REAL, embedded delimiters/newlines, Unicode, and arbitrary BLOB bytes.
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var index in indexes)
            {
                switch (reader.GetValue(index))
                {
                    case DBNull: writer.Write((byte)0); break;
                    case long integer: writer.Write((byte)1); writer.Write(integer); break;
                    case double real: writer.Write((byte)2); writer.Write(real); break;
                    case string text: writer.Write((byte)3); writer.Write(text); break;
                    case byte[] blob: writer.Write((byte)4); writer.Write(blob.Length); writer.Write(blob); break;
                    default: throw new InvalidDataException("Unsupported SQLite storage type.");
                }
            }
        }
        return Convert.ToHexString(SHA256.HashData(memory.ToArray()));
    }

    private static string HashSorted(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values.Order(StringComparer.Ordinal)) hash.AppendData(Convert.FromHexString(value));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string FileHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5,
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void CopyNewFile(string source, string destination)
    {
        // Deny simultaneous writes/deletion while copying this particular file.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void RejectJournals(string databasePath)
    {
        if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm") || File.Exists(databasePath + "-journal"))
            throw new InvalidDataException("A snapshot bundle must be finalized and contain no SQLite journal files.");
    }

    private static void RemoveCreatedFiles(string directory)
    {
        foreach (var name in new[] { DatabaseFileName, DatabaseFileName + "-wal", DatabaseFileName + "-shm",
                     DatabaseFileName + "-journal", ConfigFileName, ManifestFileName })
            File.Delete(Path.Combine(directory, name));
    }
}

public sealed record DataSnapshotManifest(int FormatVersion, DateTimeOffset CreatedAtUtc, int AppSchemaVersion,
    long SqliteUserVersion, string DatabaseSha256, string? ConfigSha256, string SchemaSha256,
    string IntegrityCheck, int ForeignKeyViolations, IReadOnlyList<SnapshotTable> Tables,
    IReadOnlyList<SnapshotMediaReference> MediaReferences);
public sealed record SnapshotColumn(string Name, string DeclaredType, int PrimaryKeyOrder);
public sealed record SnapshotTable(string Name, long RowCount, IReadOnlyList<SnapshotColumn> Columns,
    string PrimaryKeysSha256, string RowsSha256);
public sealed record SnapshotMediaReference(string Table, string RowIdentitySha256, string Column, string Path);

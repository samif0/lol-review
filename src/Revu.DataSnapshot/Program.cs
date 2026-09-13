using Revu.Core.Data;

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    PrintUsage();
    return 0;
}

if (args.Length < 2 || args[0] switch
    {
        "snapshot" or "restore" => args.Length != 3,
        "verify" => args.Length != 2,
        _ => true,
    })
{
    PrintUsage();
    return 2;
}

try
{
    foreach (var path in args.Skip(1))
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Provide fully qualified directories; there is no default data root.");

    var service = new DataSnapshotService();
    var manifest = args[0] switch
    {
        "snapshot" => service.Create(args[1], args[2]),
        "verify" => service.Verify(args[1]),
        "restore" => service.Restore(args[1], args[2]),
        _ => throw new InvalidOperationException(),
    };
    Console.WriteLine($"Verified schema {manifest.AppSchemaVersion}; {manifest.Tables.Count} tables; " +
        $"{manifest.Tables.Sum(table => table.RowCount)} rows; integrity ok; no foreign-key violations.");
    Console.WriteLine($"Database SHA-256: {manifest.DatabaseSha256}");
    Console.WriteLine($"Config: {(manifest.ConfigSha256 is null ? "absent" : "copied and verified")}. " +
        $"Media references: {manifest.MediaReferences.Count}; media bytes are not copied.");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
    or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"Snapshot operation failed: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Revu compatibility snapshot harness (explicit local directories only)");
    Console.WriteLine("  snapshot <source-data-directory> <empty-bundle-directory>");
    Console.WriteLine("  verify <bundle-directory>");
    Console.WriteLine("  restore <bundle-directory> <empty-target-data-directory>");
    Console.WriteLine("Data directories contain revu.db and optionally config.json; they are not REVU_DATA_ROOT parents.");
    Console.WriteLine("Restore never overwrites data. Media paths and configured output paths remain unchanged.");
}

# Compatibility snapshot harness

This developer tool creates an online SQLite backup and verifies a restore in a
second empty directory. It uses the existing C# data model without schema changes.
No command discovers the user's database or defaults to `AppDataPaths`.

Run with explicit, fully qualified directories that contain `revu.db` and optional
`config.json`. These are the `LoLReviewData` directories themselves; `REVU_DATA_ROOT`
is their parent and is not accepted as a substitute.

```powershell
dotnet run --project src/Revu.DataSnapshot/Revu.DataSnapshot.csproj -c Release -p:Platform=x64 -- --help
dotnet run --project src/Revu.DataSnapshot/Revu.DataSnapshot.csproj -c Release -p:Platform=x64 -- snapshot C:\RevuScratch\source\LoLReviewData C:\RevuScratch\bundle
dotnet run --project src/Revu.DataSnapshot/Revu.DataSnapshot.csproj -c Release -p:Platform=x64 -- verify C:\RevuScratch\bundle
dotnet run --project src/Revu.DataSnapshot/Revu.DataSnapshot.csproj -c Release -p:Platform=x64 -- restore C:\RevuScratch\bundle C:\RevuScratch\restored\LoLReviewData
```

The source database may be live in WAL mode. SQLite's online backup API produces
one consistent database image; no checkpoint-and-file-copy is used. The bundle is
finalized without journals. Its manifest records the application schema version,
SQLite `user_version`, integrity/foreign-key checks, schema SHA-256, table counts,
typed row and primary-key hashes for every table (including `sqlite_sequence`),
database/configuration SHA-256, and VOD/clip path references. Row hashes also cover
event identities, correction JSON, objective associations, all existing timestamps,
and opaque future columns. The current app has no general media/game offset mapping;
a synthetic extension in the tests verifies preserving numeric offsets without
adding such a schema to the product.

The current Microsoft.Data.Sqlite `BackupDatabase` call is synchronous and can
block other writers while copying. It is appropriate for this developer harness;
no claim about production backup latency is made. See the
[Microsoft API guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)
and [SQLite backup contract](https://www.sqlite.org/backup.html).

Restore verifies the bundle, rejects a schema newer than the compiled backend,
acquires the same `DataRootLease` as a cooperating sidecar, refuses all existing
target data, then copies and verifies again. The persistent owner-lock file is the
only entry allowed in an otherwise empty destination. This is a scratch-copy
restoration tool, not an in-place production rollback command. Existing backups
and `BackupService` behavior are unchanged.

Configuration is copied byte-for-byte separately from the database; there is no
cross-file transaction. Stop settings edits when a coordinated configuration
snapshot is needed. Media bytes, recordings, clip files, and external output folders
are not copied, read, rewritten, or validated. Before launching an app on the restored
root, separately isolate its media/output folders and configure `REVU_DATA_ROOT` to
the parent directory. A copied configuration still names the original output paths.
Keep bundles local: configuration and the manifest's media paths can be personal.

The lease coordinates participating C# writers and rejects junction/symlink data
roots. It cannot stop a legacy build or an unrelated SQLite client that ignores the
lease; restore's refusal to overwrite any existing data remains mandatory.

Synthetic verification:

```powershell
dotnet test src/Revu.Core.Tests/Revu.Core.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~DataSnapshotServiceTests
```

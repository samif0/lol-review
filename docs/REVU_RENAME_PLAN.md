# Revu rename — runbook

Status: **not yet executed.** This is the complete plan to move everything
named `LoLReview` to `Revu`, without losing any user data and without breaking
the auto-update chain for existing installs.

Phases are **ordered**: do them top to bottom. Each phase is independently
shippable as its own patch release. Phase 0 is cheap and high-visibility —
land it first.

> **Starting this plan in a fresh session?** Read the
> [Session handoff](#session-handoff) section at the bottom first. It lists
> the 10 gotchas, the exact baseline state, and the tacit knowledge that
> isn't in the phase steps.

---

## What we're renaming and what we are NOT

### Renaming

| Surface | From | To | Where it lives |
|---|---|---|---|
| Window title | "Revu" (already) | — | App.xaml.cs (shipped in v2.9.0) |
| Sidebar wordmark | "REVU" (already) | — | ShellPage.xaml (shipped in v2.9.0) |
| Windows Search / Start Menu entry | "LoLReview" | "Revu" | Velopack `packTitle` (Phase 0) |
| Uninstall entry ("Apps & features") | "LoLReview" | "Revu" | Velopack `packTitle` (Phase 0) |
| Desktop / Start-menu shortcut | `LoLReview.lnk` | `Revu.lnk` | Velopack `packTitle` (Phase 0) |
| Exe PE resources (`FileDescription`, `ProductName`) | "LoLReview.App" | "Revu" | csproj `<Product>` etc. (Phase 0) |
| GitHub release title | "LoL Review v..." | "Revu v..." | Workflow `--releaseName` (Phase 0) |
| C# namespaces (`LoLReview.App.*`, `LoLReview.Core.*`) | `LoLReview.*` | `Revu.*` | ~5,230 lines across 383 files (Phase 1) |
| Project file names (`LoLReview.App.csproj`, etc.) | `LoLReview.*` | `Revu.*` | 3 csprojs + 1 sln (Phase 1) |
| Source directory names (`src/LoLReview.App/`) | `LoLReview.*` | `Revu.*` | 3 directories (Phase 1) |
| Asset files (`lolreview.ico`, `.png`) | `lolreview.*` | `revu.*` | csproj reference + file rename (Phase 1) |
| AppData user data | `%LOCALAPPDATA%\LoLReviewData\lol_review.db` | `%LOCALAPPDATA%\Revu\revu.db` | AppDataPaths + migrator (Phase 2) |

### NOT renaming (deliberate)

| Surface | Why |
|---|---|
| Velopack `packId=LoLReview` | Changing this breaks the auto-update chain. Users on old version check for updates under packId "LoLReview"; a new release under "Revu" is invisible to them. Keep forever. |
| Install folder `%LOCALAPPDATA%\LoLReview\current\` | Tied to `packId`. Invisible to users (never shown in UI). Not worth the risk. |
| Git history | Old commits still say "LoLReview". History is immutable by design. |
| Existing release assets (2.0.0–2.9.x) on GitHub Releases | Leave alone. Users already downloaded them. |

---

## Phase 0 — Windows display-name override (ship FIRST)

**Goal**: users see "Revu" in Windows Search, Start Menu, shortcut name,
uninstall entry, and Task Manager. No packId change, no AppData migration.

**Risk**: low. Cosmetic. Auto-update unaffected.

**Version bump**: v2.9.3 (patch).

### Preconditions
- `v2.9.2` tag is live on GitHub (done).
- A clean working tree (`git status` shows nothing).
- Close Visual Studio (to release lock on `.vs/` cache).

### Steps

1. **Stamp exe PE resources.** Edit `src/LoLReview.App/LoLReview.App.csproj` —
   inside the first `<PropertyGroup>` after `<Version>`, add:
   ```xml
   <AssemblyTitle>Revu</AssemblyTitle>
   <Product>Revu</Product>
   <Company>Revu</Company>
   <AssemblyName>Revu.App</AssemblyName>   <!-- optional; keep OUT for now to avoid changing mainExe name -->
   ```
   **Keep `<AssemblyName>` unchanged** for Phase 0. Changing the exe filename
   requires coordinating `--mainExe` in the workflow and breaks Velopack's
   shortcut target. Defer to Phase 1.

2. **Update Velopack pack step.** Edit `.github/workflows/release.yml`,
   the `Pack Velopack release` step:
   ```yaml
   vpk pack `
     --packId LoLReview `
     --packTitle "Revu" `                     # NEW — friendly name
     --packVersion "${{ env.VERSION }}" `
     --packDir publish `
     --mainExe LoLReview.App.exe `
     --outputDir Releases
   ```
   `packId` stays `LoLReview` — do not change.

3. **Update Velopack upload step.** Same file, `Upload Velopack release to GitHub`:
   ```yaml
   vpk upload github `
     --repoUrl "https://github.com/${{ github.repository }}" `
     --token "${{ secrets.GITHUB_TOKEN }}" `
     --publish `
     --releaseName "Revu v${{ env.VERSION }}" `    # was "LoL Review v..."
     --tag "${{ env.TAG }}"
   ```

4. **Defensive shortcut cleanup.** Edit `src/LoLReview.App/Program.cs` — the
   `OnAfterUpdateFastCallback` and `OnAfterInstallFastCallback` already call
   `RemoveRedundantExeShortcut()`. Extend that helper to also remove the
   old packId-named shortcut, so upgraders from 2.9.2 don't end up with
   both `LoLReview.lnk` AND `Revu.lnk`:
   ```csharp
   private static void RemoveRedundantExeShortcut()
   {
       try
       {
           var s = new Velopack.Windows.Shortcuts();
           var loc = Velopack.Windows.ShortcutLocation.Desktop
                   | Velopack.Windows.ShortcutLocation.StartMenu;
           // old 2.9.1 legacy
           s.DeleteShortcuts("LoLReview.App.exe", loc);
           // old 2.9.2 (packId-named before packTitle override)
           s.DeleteShortcuts("LoLReview.lnk", loc);
       }
       catch { /* best-effort */ }
   }
   ```
   Note: `DeleteShortcuts` takes a relative exe path OR a shortcut file name
   in some Velopack versions — test locally first; adjust the second arg if
   the API shape differs.

5. **Bump version.** csproj `<Version>2.9.2</Version>` → `<Version>2.9.3</Version>`.

6. **Build locally**, verify zero errors:
   ```
   dotnet build src/LoLReview.App/LoLReview.App.csproj -r win-x64 -nologo -v:q
   ```

7. **Run tests**:
   ```
   dotnet test src/LoLReview.Core.Tests/LoLReview.Core.Tests.csproj
   ```

8. **Smoke-test locally** — launch via `dotnet run`, confirm nothing is broken.

9. **Commit + tag + push**:
   ```
   git add src/LoLReview.App/LoLReview.App.csproj \
           src/LoLReview.App/Program.cs \
           .github/workflows/release.yml \
           docs/REVU_RENAME_PLAN.md
   git commit -m "brand: Revu packTitle override + exe PE resources + shortcut cleanup, v2.9.3"
   git tag -a v2.9.3 -m "Revu v2.9.3 — display name flips to 'Revu' everywhere Windows shows it"
   git push origin main
   git push origin v2.9.3
   ```

10. **Wait for CI**: https://github.com/samif0/lol-review/actions. ~8-15 min.

### Validation (after CI publishes v2.9.3)

Do this on **both a fresh install** and on **an upgraded install from v2.9.2**:

- [ ] Start Menu: type "R" → **Revu** appears (not LoLReview)
- [ ] `Win+R` → `appwiz.cpl` → **Revu** listed in "Apps & features"
- [ ] Right-click installed exe → Properties → Details → **ProductName: Revu**
- [ ] Task Manager → running process shown as **Revu**
- [ ] Exactly ONE desktop shortcut, named **Revu.lnk**
- [ ] Exactly ONE Start Menu shortcut, named **Revu.lnk**
- [ ] GitHub release page shows **"Revu v2.9.3"** as the title
- [ ] Auto-update works: a v2.9.2 install prompted to update and succeeded

### Rollback
- Ship v2.9.4 that reverts workflow + csproj changes. Velopack re-creates
  shortcuts on update → old names return. packId never changed → chain intact.

---

## Phase 1 — Code identifier rename (after Phase 0 validated)

**Goal**: every `LoLReview` identifier in source → `Revu`. Source directory
names, project files, namespaces. No user-visible change (Phase 0 already
handled that); this is pure housekeeping so the code reads like the product.

**Risk**: low. Mechanical search/replace plus rebuild.

**Version bump**: v2.10.0 (minor — arbitrary, but a "namespace rename" feels
worth a minor number).

### Preconditions
- Phase 0 (v2.9.3) shipped and validated.
- Clean working tree.
- Close Visual Studio.
- **Back up** `lol_review.db` from `%LOCALAPPDATA%\LoLReviewData\` to a
  safe location outside the repo. Phase 1 doesn't touch it, but we want the
  safety net anyway.

### Steps

#### 1.1 — Dry-run inventory
Preview every line that will change:
```sh
grep -rnE "LoLReview" \
  --include="*.cs" --include="*.csproj" --include="*.sln" \
  --include="*.xaml" --include="*.json" --include="*.md" \
  --exclude-dir=bin --exclude-dir=obj --exclude-dir=node_modules \
  --exclude-dir=.git --exclude-dir=.vs --exclude-dir=Releases \
  --exclude-dir=packages \
  > /tmp/loreview-refs.txt
wc -l /tmp/loreview-refs.txt
```
Expect ~5,600 lines total. Spot-check for anything that should NOT change
(e.g. references inside git history URLs, release asset paths).

#### 1.2 — Automated replace
Write a PowerShell script or run this (bash):
```sh
# Case-sensitive: LoLReview → Revu, lolreview → revu, lol-review → revu, lol_review → revu
find . -type f \( -name "*.cs" -o -name "*.csproj" -o -name "*.sln" \
                  -o -name "*.xaml" -o -name "*.json" -o -name "*.md" \
                  -o -name "*.yml" \) \
  -not -path "./bin/*" -not -path "./obj/*" -not -path "./.git/*" \
  -not -path "./node_modules/*" -not -path "./.vs/*" \
  -not -path "./Releases/*" -not -path "*/bin/*" -not -path "*/obj/*" \
  -exec sed -i \
    -e 's/LoLReview/Revu/g' \
    -e 's/lolreview/revu/g' \
    -e 's/lol-review/revu/g' \
  {} \;
```
**Do NOT substitute `lol_review`** — that's the DB filename, handled in Phase 2.
**Do NOT substitute in `.github/workflows/release.yml`** `--packId` line —
keep `packId=LoLReview`. Easiest way: add a step to revert that specific line
after the sed pass:
```sh
# Restore packId=LoLReview after the bulk replace
sed -i 's/--packId Revu/--packId LoLReview/' .github/workflows/release.yml
```
Also revert any references to the install-folder path (`%LOCALAPPDATA%\LoLReview\`)
that AppDataPaths.cs uses — those stay in Phase 1 (still read by the old
bootstrapper until Phase 2 migrates).

#### 1.3 — Rename directories
```sh
git mv src/LoLReview.App src/Revu.App
git mv src/LoLReview.Core src/Revu.Core
git mv src/LoLReview.Core.Tests src/Revu.Core.Tests
git mv LoLReview.sln Revu.sln
```

#### 1.4 — Rename project files
```sh
git mv src/Revu.App/LoLReview.App.csproj src/Revu.App/Revu.App.csproj
git mv src/Revu.Core/LoLReview.Core.csproj src/Revu.Core/Revu.Core.csproj
git mv src/Revu.Core.Tests/LoLReview.Core.Tests.csproj src/Revu.Core.Tests/Revu.Core.Tests.csproj
```

#### 1.5 — Rename asset files
```sh
git mv src/Revu.App/Assets/lolreview.ico src/Revu.App/Assets/revu.ico
git mv src/Revu.App/Assets/lolreview.png src/Revu.App/Assets/revu.png
```
Then update csproj references (the sed pass probably already did this but
verify):
- `<ApplicationIcon>Assets\revu.ico</ApplicationIcon>`
- `<Content Include="Assets\revu.ico" ...>`
- `<Content Include="Assets\revu.png" ...>`

And update `App.xaml.cs` where `ApplyWin32Icon` reads the path:
```csharp
var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "revu.ico");
```

#### 1.6 — Nuke IDE cache
```sh
rm -rf .vs/
rm -rf src/*/bin src/*/obj
```
Already gitignored; just deletes local state so VS rebuilds it on next open.

#### 1.7 — Update workflow paths
`.github/workflows/release.yml` — fix every path the sed pass rewrote
incorrectly or missed:
- `src/LoLReview.Core.Tests/LoLReview.Core.Tests.csproj` → `src/Revu.Core.Tests/Revu.Core.Tests.csproj`
- `src/LoLReview.App/LoLReview.App.csproj` → `src/Revu.App/Revu.App.csproj`
- `publish\LoLReview.App.exe` — consider renaming to `Revu.App.exe` ONLY if
  you also set `<AssemblyName>Revu.App</AssemblyName>` in csproj AND update
  `--mainExe` in the Velopack pack step. **Easier path: leave `AssemblyName`
  as `LoLReview.App` for now.** Deferring the exe filename rename avoids
  coordinating with `--mainExe`, shortcut target paths, and every user's
  installed `%LOCALAPPDATA%\LoLReview\current\LoLReview.App.exe` shortcut
  target.

#### 1.8 — Build locally
```sh
dotnet build Revu.sln -c Debug -v:q
```
Must be zero errors. If there are missing-reference errors, the sed pass
missed something — grep for it, fix, retry.

#### 1.9 — Run tests
```sh
dotnet test src/Revu.Core.Tests/Revu.Core.Tests.csproj
```
All 35 must pass.

#### 1.10 — Smoke-test
```sh
LOLREVIEW_DIAG_LOGS=1 dotnet run --project src/Revu.App/Revu.App.csproj -r win-x64
```
Click through every page — Dashboard, Settings, Tilt Check, Onboarding
(logout first if needed), Review, VOD Player if you have VODs. No missing
XAML resources, no DllNotFound exceptions, no crashes.

#### 1.11 — Bump version, commit, tag, push
csproj `<Version>2.9.3</Version>` → `<Version>2.10.0</Version>`.

```sh
git add -A
git commit -m "rename: LoLReview → Revu for all source identifiers, v2.10.0"
git tag -a v2.10.0 -m "Revu v2.10.0 — namespace + folder + asset file rename"
git push origin main
git push origin v2.10.0
```

### Validation

- [ ] CI workflow builds + publishes v2.10.0 release
- [ ] `Revu-Setup.exe` (or whatever the asset is named) installs cleanly on a
      clean VM
- [ ] Installed exe launches, all pages render
- [ ] Auto-update from v2.9.3 → v2.10.0 works (Velopack packId unchanged)
- [ ] `grep -r LoLReview src/` returns only intentional residuals
      (packId strings in workflow, install-folder paths in AppDataPaths)

### Rollback
- Git revert the big rename commit. CI rebuilds under old names.
- If already deployed and users are on v2.10.0: ship v2.10.1 as a no-op
  bump (don't roll back the rename — too much churn for a revert that
  users can't see).

---

## Phase 2 — AppData migration (after Phase 1 validated)

**Goal**: move user data from `%LOCALAPPDATA%\LoLReviewData\lol_review.db` to
`%LOCALAPPDATA%\Revu\revu.db` on first launch of the new build. Zero data
loss, old folder preserved as a named backup.

**Risk**: **HIGH.** This touches user data. The memory rule says "NEVER
overwrite the AppData DB." We honor it by only ever copying, never
overwriting, and keeping the old folder as a backup.

**Version bump**: v2.11.0.

### Preconditions
- Phase 1 (v2.10.0) shipped and validated.
- **Back up your own `lol_review.db`** before testing locally. The migrator
  is defensive but bugs happen.
- Ideally, clone your DB to a scratch user folder and test the migrator
  against that copy before writing the real code path.

### Steps

#### 2.1 — Update AppDataPaths
`src/Revu.Core/Data/AppDataPaths.cs`:
```csharp
// NEW
public static string InstallRoot =>
    Path.Combine(LocalAppDataRoot, "Revu");       // was: "LoLReview"
public static string UserDataRoot =>
    Path.Combine(LocalAppDataRoot, "Revu");       // was: "LoLReviewData"; collapse to ONE folder
public static string DatabasePath =>
    Path.Combine(UserDataRoot, "revu.db");        // was: lol_review.db
```
**Note**: the old code had TWO separate folders (`LoLReview` for logs,
`LoLReviewData` for DB). Collapsing them to a single `Revu` folder is
cleaner but means the migrator has to copy files from both sources.
Pick ONE of these paths:
- **(a) Collapse** — all new data under `%LOCALAPPDATA%\Revu\`. DB +
  logs + clips + backups all in one tree. Migrator copies from both old
  folders.
- **(b) Preserve separation** — `%LOCALAPPDATA%\Revu\` for logs/install,
  `%LOCALAPPDATA%\RevuData\` for user data. Migrator copies 1:1.
**Recommendation: (b).** Less migration surface, simpler code.

#### 2.2 — Write `AppDataMigrator`
New file `src/Revu.Core/Data/AppDataMigrator.cs`:
```csharp
public static class AppDataMigrator
{
    public static MigrationResult RunIfNeeded(ILogger logger)
    {
        var newDb = AppDataPaths.DatabasePath;
        var oldDb = Path.Combine(LocalAppDataRoot, "LoLReviewData", "lol_review.db");

        if (File.Exists(newDb))
            return MigrationResult.AlreadyMigrated;

        if (!File.Exists(oldDb))
            return MigrationResult.FreshInstall;

        // Migrate.
        Directory.CreateDirectory(Path.GetDirectoryName(newDb)!);
        File.Copy(oldDb, newDb, overwrite: false);
        TryCopy(oldDb + "-shm", newDb + "-shm");
        TryCopy(oldDb + "-wal", newDb + "-wal");

        // Verify by smoke-querying the new file.
        try
        {
            using var conn = new SqliteConnection($"Data Source={newDb};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM games";
            var n = (long)cmd.ExecuteScalar();
            logger.LogInformation("Migrated DB contains {N} games", n);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration smoke-query failed; rolling back");
            SafeDelete(newDb);
            SafeDelete(newDb + "-shm");
            SafeDelete(newDb + "-wal");
            return MigrationResult.SmokeFailedRolledBack;
        }

        // Rename old folder to a timestamped backup so user can recover.
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var oldDir = Path.Combine(LocalAppDataRoot, "LoLReviewData");
        var backupDir = Path.Combine(LocalAppDataRoot, $"LoLReviewData.migrated-backup-{ts}");
        try { Directory.Move(oldDir, backupDir); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not rename old folder; leaving in place"); }

        return MigrationResult.Migrated;
    }

    // Helpers: TryCopy, SafeDelete, etc.
}

public enum MigrationResult
{
    FreshInstall,
    AlreadyMigrated,
    Migrated,
    SmokeFailedRolledBack,
}
```

#### 2.3 — Call the migrator at startup
`src/Revu.App/App.xaml.cs` or the bootstrapper, **BEFORE** any SQLite
connection is opened:
```csharp
var migratorLogger = ...;
var result = AppDataMigrator.RunIfNeeded(migratorLogger);
AppDiagnostics.WriteVerbose("startup.log", $"AppData migration result: {result}");
```

#### 2.4 — Test against a real DB
- Copy your own `lol_review.db` to a scratch Windows user's folder.
- Set `%LOCALAPPDATA%` to that scratch user's LocalAppData (or test in a VM).
- Run the app. Verify in the log: `"AppData migration result: Migrated"`.
- Verify game count, review count, tilt-check count, rules count all match.
- Verify the old folder is now named `LoLReviewData.migrated-backup-<ts>`.

#### 2.5 — Test failure paths
- **No old data**: rename your scratch LoLReviewData folder to something
  else, launch. Log should say `FreshInstall`. App opens empty but functional.
- **Old data is corrupt**: truncate the old db to 0 bytes, launch. Migration
  runs, smoke-query fails, logs `SmokeFailedRolledBack`, newDb is deleted,
  app falls back to reading the old (broken) DB. User sees schema-init
  errors but data isn't destroyed.
- **Already migrated**: run twice in a row. Second run logs `AlreadyMigrated`.

#### 2.6 — Bump, commit, tag, push
```
<Version>2.11.0</Version>
git add -A
git commit -m "migration: LoLReviewData/lol_review.db → Revu/revu.db with copy+verify+backup, v2.11.0"
git tag -a v2.11.0 -m "Revu v2.11.0 — user data migrates to Revu folder; old folder preserved as backup"
git push origin main
git push origin v2.11.0
```

### Validation (after CI publishes v2.11.0)

On a real **upgraded** install (not a fresh one):
- [ ] Launch app, go to Settings
- [ ] All games / reviews / rules / objectives / tilt-checks still visible
- [ ] Adherence streak unchanged from pre-upgrade
- [ ] `%LOCALAPPDATA%\Revu\revu.db` exists
- [ ] `%LOCALAPPDATA%\LoLReviewData.migrated-backup-<ts>` exists (preserved)
- [ ] Startup log shows `"AppData migration result: Migrated"`
- [ ] Next launch shows `"AlreadyMigrated"` (idempotent)

### Rollback
- If migration breaks on users: ship a fix release that explicitly restores
  the old path (points `DatabasePath` back at `%LOCALAPPDATA%\LoLReviewData\lol_review.db`).
  Their data was preserved — it's still in the backup folder.
- For users who DID migrate successfully: we can't roll them back without
  data loss. Leave them on the new path.

---

## Phase 3 — Cosmetic tail (after Phase 2 stable)

**Goal**: finish the long tail of "LoLReview" references in docs, mockups,
misc scripts.

**Risk**: zero. Pure documentation.

**Version bump**: none (docs-only commit).

### Steps
- README.md, docs/*.md — replace "LoLReview" with "Revu" in prose
- Mockup file `mockups/app-mockup.html` if it has title strings
- `scripts/*.ps1` / `*.py` — any hardcoded paths that mention old folders
- `.claude/` memory files (optional) — low value

### Commit
```
git add docs/ README.md mockups/
git commit -m "docs: last LoLReview → Revu references in prose"
git push origin main
```

No version tag — just a docs commit.

---

## Total cost

| Phase | Time | Risk | User-visible change |
|---|---|---|---|
| 0 | ~1h | Low | Start Menu, Apps & features, shortcut, Task Manager all say "Revu" |
| 1 | ~3h | Low | None (code-only) |
| 2 | ~3h incl. testing | HIGH | AppData folder renamed on upgrade |
| 3 | ~30m | None | Docs updated |
| **Total** | **~7-8 hours** | | |

---

## Hard rules for whoever executes this

1. **Never execute Phase 2 without first backing up your own `lol_review.db`
   to a safe location outside the repo.** Memory rule #1.
2. **Never change `packId` in the workflow.** Auto-update breaks for everyone.
3. **One phase per release.** Don't bundle Phase 1 + Phase 2 in the same
   version bump — if Phase 2 has a bug, rolling back Phase 1 simultaneously
   is gratuitous churn.
4. **Test upgrade paths, not just fresh installs.** Every release must be
   validated against a running v2.9.x install that upgrades.
5. **Keep the `LoLReviewData.migrated-backup-<ts>` folder around for at
   least 2-3 releases.** Don't ship an auto-cleanup that deletes it —
   users need a recovery path.

---

## Open questions to resolve before starting Phase 1

- Should `<AssemblyName>` change to `Revu.App`? (currently stays as `LoLReview.App`
  to keep `--mainExe` stable in the workflow.) If yes, coordinate with Velopack
  `--mainExe` flag; existing shortcuts on 2.9.x point at `LoLReview.App.exe`
  path, so the installed shortcut target would need to be rebuilt.
  **Recommendation: leave as-is for Phase 1. Revisit as a Phase 4 if it bothers you.**

- Phase 2 folder strategy: collapse (one `Revu` folder) or preserve separation
  (`Revu` + `RevuData`)? **Recommendation: preserve separation — simpler
  migrator.**

- Cleanup of the `LoLReviewData.migrated-backup-*` folder: when, if ever?
  **Recommendation: never auto-cleanup. Document in README that users can
  delete it manually if they want the disk space.**

---

## Session handoff

Everything a fresh session needs that isn't obvious from the phase steps.
Read before starting Phase 0.

### Baseline state (as of the session that wrote this doc)

Confirm these match before touching anything. If they don't, someone has
done work in the meantime and the plan may be stale.

```sh
# Expected version
grep '<Version>' src/LoLReview.App/LoLReview.App.csproj
# → <Version>2.9.2</Version>

# Expected tag
git tag -l "v2.9*"
# → v2.9.0, v2.9.1, v2.9.2

# Expected test count + clean build
dotnet test src/LoLReview.Core.Tests/LoLReview.Core.Tests.csproj --nologo -v:q 2>&1 | tail -3
# → Passed! Failed: 0, Passed: 35, Skipped: 0, Total: 35

dotnet build src/LoLReview.App/LoLReview.App.csproj -r win-x64 -nologo -v:q 2>&1 | tail -3
# → Build succeeded. ~349 warnings (all MVVMTK0045 AOT-compat noise; ignore). 0 errors.
```

Also verify the installed app and user data:
```sh
ls %LOCALAPPDATA%\LoLReview\current\LoLReview.App.exe        # installed exe
ls %LOCALAPPDATA%\LoLReviewData\lol_review.db                 # user DB
```

### What's already shipped (don't redo)

v2.9.0–v2.9.2 already fixed:
- Window title = "Revu" (App.xaml.cs)
- Sidebar wordmark = "REVU" (ShellPage.xaml)
- Loading screen text = "Revu - Loading..."
- VodPlayer hint mentions "Revu"
- Custom HUD title bar (empty drag region; sidebar spans into title-bar row)
- New `R` logo in `Assets/lolreview.ico` + `.png` (the FILES are new; the
  NAMES still say "lolreview" and get renamed in Phase 1)
- Taskbar-icon fix via `WM_SETICON` P/Invoke (`App.xaml.cs::ApplyWin32Icon`)
- Explicit `<Content Include="Assets\lolreview.ico" CopyToOutputDirectory=...>`
  in csproj so incremental build reliably copies the icon
- Shortcut-dedup helper `RemoveRedundantExeShortcut()` in Program.cs
  targeting `LoLReview.App.exe`

**Do NOT redo any of these in Phase 0.** Phase 0 adds Velopack `packTitle`
+ exe PE resources (`<Product>`, `<AssemblyTitle>`, `<Company>`). That's it.

### 10 gotchas that cost me real time this session

Fresh session will hit these. Preempt them.

#### 1. Hidden U+0090 control characters in XAML comments

Several XAML files have literal U+0090 (UNICODE NEXT LINE control char)
between bullet glyphs in comments like `<!-- ••• Save Button Row ••• -->`.
Example file: `src/LoLReview.App/Views/SettingsPage.xaml`.

**Impact**: `Edit` tool matches fail because the tool doesn't include U+0090
when you paste the "visible" version of the comment. Similarly, `sed`
rewrites may leave the bullets but remove or mangle the control char.

**Workaround**: anchor `Edit` / `sed` operations on plain-ASCII lines
nearby (`<Grid>`, named elements, etc.), not on the bulleted comment.
After Phase 1's sed pass, `grep -Pn '\x90' src/**/*.xaml` to confirm
nothing was accidentally corrupted.

#### 2. Mixed CRLF/LF line endings

Git will warn `LF will be replaced by CRLF` on many files. Windows users
running bash via Git-Bash see inconsistent line endings; .NET/MSBuild
doesn't care but diffs look noisy.

**Workaround**: either run `git config core.autocrlf true` once, or
accept the noise. Don't bulk-convert with dos2unix — some files are
legitimately CRLF and converting them breaks round-trip checksums.

#### 3. `lol_review.db` is NOT a naming candidate in Phase 1

Phase 1 sed rewrites `lolreview` → `revu`. It must NOT rewrite `lol_review`
(the DB filename) — that's Phase 2's job, coordinated with the migrator
so the old path is still readable.

**Exact sed allowlist** (what to substitute):
- `LoLReview` → `Revu` (case-sensitive)
- `lolreview` → `revu` (lowercase form in asset filenames)
- `lol-review` → `revu` (hyphen form in some URLs/slugs)

**Exact sed DENY list** (do not substitute):
- `lol_review` (the DB filename — Phase 2)
- `--packId LoLReview` in `.github/workflows/release.yml` (auto-update)
- Path string literals in `AppDataPaths.cs` that refer to the OLD folders
  — after Phase 1, those literals become constants used by the Phase 2
  migrator. If Phase 1 rewrites them, the migrator has no way to find
  the user's old DB.

Recommended approach: exclude `src/LoLReview.Core/Data/AppDataPaths.cs` and
`.github/workflows/release.yml` from the bulk sed pass. Patch those two
files by hand with the intended edits.

#### 4. The workflow file has implicit path references

`.github/workflows/release.yml` has string literals like:
- `src/LoLReview.App/LoLReview.App.csproj` — will be rewritten by sed
- `publish\LoLReview.App.exe` — will be rewritten (fine IF AssemblyName
  also changed; DON'T change AssemblyName in Phase 1)
- `--mainExe LoLReview.App.exe` — MUST NOT change unless you also change
  the csproj's `<AssemblyName>` in lock-step
- `--packId LoLReview` — must stay

Before Phase 1's sed: **read the workflow end-to-end**, decide per-line
whether the rewrite is correct. Safer: patch the workflow by hand after
exclusion.

#### 5. Velopack API shape check before trusting the migrator

The doc's `DeleteShortcuts("LoLReview.App.exe", loc)` call may be wrong.
Velopack 0.0.1298's method signature can take a relative exe path, a
shortcut name, or an absolute path depending on overload.

**How to verify before coding**:
```sh
grep -A10 "M:Velopack.Windows.Shortcuts.DeleteShortcuts" \
  ~/.nuget/packages/velopack/0.0.1298/lib/net8.0/Velopack.xml
```
Read the doc comment for the exact parameter name and semantics. Write
a one-off console call locally and inspect what actually gets deleted
before trusting the callback in Program.cs.

#### 6. Phase 2's migrator must read the OLD path

In Phase 1 we rewrite most `LoLReview` references. But the Phase 2
migrator needs to know where the OLD data lived. Plan:

- Phase 1: **do not** sed `AppDataPaths.cs`. Let `InstallRoot` and
  `UserDataRoot` still point at the old names after Phase 1.
- Phase 2: introduce `AppDataMigrator` with hardcoded OLD-path constants
  (`"LoLReviewData"`, `"lol_review.db"`) AND update `AppDataPaths.cs`
  to the new `Revu`/`revu.db` paths in the same commit.

If Phase 1 touches `AppDataPaths.cs`, the Phase 2 migrator is blind.

#### 7. Riot proxy URL ≠ packId

`src/LoLReview.Core/Services/RiotProxyEndpoint.cs` has a hardcoded URL:
```csharp
public const string BaseUrl = "https://revu-proxy.lol-review.workers.dev";
```
The `lol-review.workers.dev` part is Cloudflare's **account subdomain** —
unrelated to packId or the rename. Leave this URL alone unless you
deliberately rename the Cloudflare workers.dev subdomain (separate
decision; affects auth endpoints, proxy routing, breaks existing users
who have the old URL baked in their installed app).

#### 8. `.vs/lol-review/` may or may not be tracked

```sh
git ls-files .vs/lol-review/  # if this returns anything, it's tracked
```
If tracked: `git rm --cached -r .vs/lol-review/` before renaming.
If not tracked (expected): it regenerates on next VS open. Ignore.

#### 9. Clean build is mandatory before smoke-test

Incremental build after a namespace rename WILL produce stale artifacts
that compile but reference missing types at runtime. Always:
```sh
rm -rf src/*/bin src/*/obj
dotnet build ...
```
This also fixes the "embedded icon stays old" problem — the clean build
forces the PE resource re-embed.

#### 10. CI vs local build differences

GitHub Actions uses a fresh checkout so the `bin/`/`obj/` mtime traps
don't bite. Locally they do. **If CI passes but local fails, or vice
versa, default to trusting CI.** Local failures are usually mtime/cache;
CI is the source of truth.

### Velopack quick-reference for this project

Installed at `~/.dotnet/tools/vpk`. Version `0.0.1298`.

Key flags we use / might use:
- `--packId` — stable identifier, never change (our value: `LoLReview`)
- `--packTitle` — Windows display name (Phase 0 sets this to `Revu`)
- `--packVersion` — SemVer, driven by the git tag
- `--mainExe` — current: `LoLReview.App.exe`; must match the actual exe
  filename in the publish dir
- `--icon` — we don't pass this; Velopack picks up from `ApplicationIcon`
  in csproj
- `--shortcuts` — default `Desktop,StartMenuRoot` (we leave default)

API namespace: `Velopack.Windows.Shortcuts`. XML docs in
`~/.nuget/packages/velopack/0.0.1298/lib/net8.0/Velopack.xml`.

### Cloudflare proxy facts (for context, not Phase targets)

- Worker name: `revu-proxy`
- URL: `https://revu-proxy.lol-review.workers.dev`
- Secrets: `RIOT_API_KEY`, `ALLOWED_TOKENS`, `RESEND_API_KEY` (all set)
- D1 database: `revu-db`
- Resend sender: `login@revu.lol` (domain verified)

None of this changes during the rename phases.

### If you get stuck

- `git log --oneline -20` shows recent work — read the session commits
  (commits `c7c5b4c` through `7c81ca2` are the Revu brand work).
- `git show v2.9.2 --stat` shows exactly what shipped in the last release.
- `startup.log` at `%LOCALAPPDATA%\LoLReview\startup.log` has `LOLREVIEW_DIAG_LOGS=1`
  verbose output if the dev client is run with that env var set — includes
  LCU phase transitions, champ-select state, migration results (once Phase 2
  ships).
- `docs/REVU_RENAME_PLAN.md` — this file. Always the most up-to-date plan.

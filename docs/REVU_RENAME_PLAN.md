# Revu rename plan — from LoLReview identifiers to Revu

Status: **not yet executed**. This is the design doc for a multi-phase rename that
moves every `LoLReview` identifier to `Revu` without losing user data.

## Honest inventory

| Layer | Count | Risk |
|---|---|---|
| `namespace LoLReview.*` + `using LoLReview.*` in .cs | ~5,230 lines across 383 files | Low (mechanical) |
| `xmlns:vm="using:LoLReview.App..."` in .xaml | ~369 lines across 116 files | Low |
| `.csproj` + `.sln` identifiers | 3 projects, 1 solution | Low |
| Directory + file names (`src/LoLReview.App/`, `.sln`) | ~10 paths | Medium (git `mv`, IDE state) |
| **AppData folders** (`%LOCALAPPDATA%\LoLReview\` + `LoLReviewData\`) | 2 per user | **HIGH** — memory rule: NEVER overwrite AppData DB |
| `lol_review.db` filename | 1 per user | **HIGH** |
| Velopack `packId=LoLReview` | 1 line in workflow | **HIGH** — changing breaks the auto-update chain |
| Installed app folder `%LOCALAPPDATA%\LoLReview\current\...` | 1 per user | HIGH — Velopack-managed |
| Release asset naming (`LoLReview-Setup.exe`) | baked into Velopack packId | Same |
| Docs / README content | ~6 markdown files | Low |
| Workflow release-name string | 1 line | Low |

## Tiered view

### Tier 1: Cosmetic user-facing (safe; mostly done in v2.9.0/.1)
- Window title, loading screen, sidebar wordmark, docs, workflow release title

### Tier 2: Code identifiers (big surface, zero user impact)
- `namespace LoLReview.*` → `namespace Revu.*`
- `RootNamespace`, csproj/sln filenames, directory names
- ~3 hours mechanical work; build passes at the end; user sees zero difference

### Tier 3: Persistent user state (dangerous)
- `%LOCALAPPDATA%\LoLReviewData\lol_review.db` → `%LOCALAPPDATA%\Revu\revu.db`
- Velopack `packId=LoLReview` → `Revu` (we are explicitly NOT doing this)
- Shortcut / Start Menu name
- Wrong move = data loss or two parallel installs

## The dangerous part

### AppData folder + DB rename

**Risk**: existing users have `%LOCALAPPDATA%\LoLReviewData\lol_review.db` full of months of data.
If the next release looks for `%LOCALAPPDATA%\Revu\revu.db`, they start from empty and
think the app deleted everything.

**Safe migration** (on first launch of the renamed release, BEFORE opening any
SQLite connection):

1. If new path exists → normal boot (trust; never overwrite).
2. If new path doesn't exist AND old path exists → copy-then-verify-then-rename-old:
   - create new dir
   - copy `lol_review.db` + `.db-shm` + `.db-wal` to the new location (as
     `revu.db` et al.)
   - open a read-only connection to the new file, smoke query
     `SELECT COUNT(*) FROM games`
   - smoke success → rename old dir to `LoLReviewData.migrated-backup-<timestamp>`
     so the user has a manual-recovery fallback; **do not delete**
   - smoke failure → delete the new file, leave old alone, log the failure,
     keep running against the old location as a fallback
3. Log counts to `startup.log`:
   "Migrated N games, M reviews, K session rows from LoLReviewData → Revu".
4. Test with a real production-sized DB backup before shipping.

This honors the memory rule ("NEVER overwrite AppData DB") — the migration is
additive; the old folder becomes a named backup, not garbage.

### Velopack packId rename — explicitly NOT doing this

**Risk**: Velopack reads `packId` to ask the release feed "is there a newer version
of X?". If we ship v3.0.0 with `packId=Revu`, existing users on v2.9.1 never find
out — their installed app still says "I am LoLReview, check for LoLReview updates."

**Decision: keep `packId=LoLReview` forever.** It's invisible in the UI, auto-update
chain stays intact, zero user action required. The only time to revisit is if a
distribution channel requires the packId to match the branded name.

## Execution plan

### Phase 1: code identifier rename (~3h, low risk)
1. **Script-driven `sed`** over `.cs`, `.csproj`, `.sln`, `.xaml`, `.json`, `.md`
   under the repo. Exclude `bin/`, `obj/`, `node_modules/`, `packages/`, `.git/`,
   `Releases/`, `.vs/`. Dry-run first.
2. **Rename directories + files** via `git mv` so history is preserved:
   - `src/LoLReview.App/` → `src/Revu.App/`
   - `src/LoLReview.Core/` → `src/Revu.Core/`
   - `src/LoLReview.Core.Tests/` → `src/Revu.Core.Tests/`
   - each `*.csproj` and `LoLReview.sln` → `Revu.sln`, etc.
   - `Assets/lolreview.ico/.png` → `Assets/revu.ico/.png`
3. **Update `.github/workflows/release.yml`** to reference the new paths +
   project names. Keep `--packId LoLReview`.
4. **Rebuild**; must be zero errors. Run Core tests (35).
5. **Nuke `.vs/`** — Visual Studio workspace cache regenerates on next open.
   Already gitignored.
6. **Smoke-test the app** — launch, log in, navigate every page, verify no
   missing-resource errors.
7. **Commit + push** — single big commit or broken by file type.

### Phase 2: AppData folder migration (~2h, medium risk if tested)
1. Update `AppDataPaths.cs`:
   - `InstallRoot` → `%LOCALAPPDATA%\Revu`
   - `UserDataRoot` → `%LOCALAPPDATA%\RevuData` (or collapse to one `Revu` folder)
   - `DatabasePath` → `...\revu.db`
2. New `AppDataMigrator.cs`, run once in the bootstrapper:
   ```
   if !exists(newDbPath) and exists(oldDbPath):
       copy oldDbPath → newDbPath
       copy -shm and -wal too
       open read-only, SELECT COUNT(*) FROM games
       if fails: delete newDbPath, log, keep running against old
       if succeeds: rename old folder → LoLReviewData.migrated-backup-<ts>
       log counts
   ```
3. Test with a real DB: restore from backup, run, verify all games/reviews/
   notes/rules/tiltchecks visible.
4. Preserve fallback: old folder stays until user (or later cleanup release)
   deletes it.
5. Ship behind normal version bump.

### Phase 3: cosmetic tail (~30 min)
- Rename `lolreview.ico/.png` → `revu.ico/.png` (asset files; refs in csproj
  change; icon content unchanged)
- Workflow release-name string → `"Revu v${{ env.VERSION }}"`
- Update README + docs content

## Explicitly NOT in the plan
- packId rename — keep `LoLReview` forever, invisible, auto-update works
- Installed-folder rename (`%LOCALAPPDATA%\LoLReview\current\`) — tied to packId
- Shortcut / Start Menu display-name change — carries its own "do both shortcuts
  exist during transition" risk; defer
- Namespace-level public-API changes — there is no public API; internal app only

## Total scope, honest
- ~5 hours of focused work for Phase 1 + 2 + 3
- Zero user data loss if Phase 2 is tested against a real DB backup first
- Zero interrupted installs — packId stays the same
- Downside: `grep LoLReview` won't find current code; old commit history still
  shows it (correct — history should not be rewritten)

## Preconditions before starting
- Backup the current `lol_review.db` somewhere safe (not inside the repo)
- Confirm no other branch / PR is touching these paths
- Close Visual Studio (to release lock on `.vs/` cache)

## Commit structure
- One commit per phase, in order. Rollback is easier if Phase 2 migration has
  issues on one user:
  - Phase 1: `rename: LoLReview identifiers → Revu across src + workflow`
  - Phase 2: `migration: LoLReviewData/lol_review.db → Revu/revu.db, safe copy+verify`
  - Phase 3: `brand: rename asset files and workflow strings to Revu`

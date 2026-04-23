# Codebase Onboarding

This document is for future sessions that need to get productive in this repo fast without re-discovering the architecture, storage layout, and known traps.

## What This App Is

LoL Review is a local-first WinUI 3 desktop app for League of Legends review.

- It watches the League client through the LCU API.
- It prompts before queue / after game.
- It stores almost everything in a local SQLite database.
- It supports VOD matching, review history, objectives, rules, analytics, and updater/release packaging through Velopack.

## Repo Map

Top-level structure:

- `src/Revu.App`
  WinUI app shell, views, viewmodels, navigation, updater UI, dialogs, theming.
- `src/Revu.Core`
  SQLite data layer, repositories, migrations, LCU integration, domain services.
- `.github/workflows/release.yml`
  GitHub Actions build + Velopack release pipeline.
- `README.md`
  Install/build/release basics.
- `data/`
  Repo-local dev data only. Real installed-user data lives under `%LOCALAPPDATA%`.

## Read This First

If you only read a few files at the start of a session, read these in order:

1. `README.md`
2. `src/Revu.App/App.xaml.cs`
3. `src/Revu.Core/Data/AppDataPaths.cs`
4. `src/Revu.Core/Data/DatabaseInitializer.cs`
5. `src/Revu.Core/Lcu/GameMonitorService.cs`
6. `src/Revu.Core/Lcu/StatsExtractor.cs`
7. `src/Revu.Core/Services/GameService.cs`
8. `src/Revu.Core/Data/Repositories/GameRepository.cs`
9. `src/Revu.App/ViewModels/ReviewViewModel.cs`
10. `src/Revu.Core/Services/VodService.cs`
11. `src/Revu.App/Services/UpdateService.cs`

That sequence gives you startup, storage, migrations, game ingest, review persistence, VODs, and updater behavior.

## Runtime Architecture

There are two major layers:

- `Revu.App`
  UI composition, DI bootstrap, navigation, system tray, updater UI, dialogs, page/viewmodel logic.
- `Revu.Core`
  Data persistence, business logic, LCU polling, match parsing, VOD matching, config, backups, analytics.

Dependency direction is simple:

- `Revu.App` depends on `Revu.Core`
- `Revu.Core` does not depend on `Revu.App`

### App startup

Startup is controlled by `Program.cs` and `App.xaml.cs`.

- `Program.cs` runs Velopack hooks first. This is critical for install/update/uninstall behavior.
- `App.xaml.cs` creates the host, initializes dispatcher helpers, migrates legacy data, runs integrity checks, creates safety backups, runs DB initialization, then starts the shell.
- DI registration also lives in `App.xaml.cs`.

Important startup order in `App.xaml.cs`:

1. `LegacyDatabaseMigrationService`
2. `DatabaseIntegrityChecker`
3. `BackupService.CreateSafetyBackupAsync`
4. `BackupService.RunBackupAsync`
5. `DatabaseInitializer.InitializeAsync`
6. Shell page creation
7. Hosted services start, including `GameMonitorService`

## Storage Model

There is a hard split between install-owned files and user-owned files.

From `AppDataPaths.cs`:

- Install root: `%LOCALAPPDATA%\\LoLReview` (Velopack-owned, matches packId)
- User data root: `%LOCALAPPDATA%\\LoLReviewData`
- Database: `%LOCALAPPDATA%\\LoLReviewData\\revu.db`
- Config: `%LOCALAPPDATA%\\LoLReviewData\\config.json`
- Backups: `%LOCALAPPDATA%\\LoLReviewData\\backups`
- Clips: `%LOCALAPPDATA%\\LoLReviewData\\clips`

This split matters a lot:

- reinstalling/updating the app should not wipe user data
- manual DB surgery should happen in `LoLReviewData`, not under the install root
- updater/package files live under `%LOCALAPPDATA%\\LoLReview\\packages`

## Database Layer

### Initialization and migration

`DatabaseInitializer.cs` is the main schema normalizer.

- schema creation statements come from `Schema.cs`
- additive migrations come from `Schema.AllMigrations`
- initializer also rewrites legacy/hybrid tables to the current schema where necessary
- initializer seeds concept tags, derived event definitions, and persistent notes
- initializer backfills some objective state

This file is a real hotspot. Many "why is this old DB weird?" issues end here.

### Important repositories

- `GameRepository`
  Main game row CRUD, review persistence, history queries, analytics-facing aggregates.
- `SessionLogRepository`
  Session log rows, adherence streak calculation, mental/rule analytics.
- `ObjectivesRepository`
  Objective persistence and progress.
- `RulesRepository`
  Saved rules and rule evaluation helpers.
- `MatchupNotesRepository`
  Matchup notes keyed by champion/enemy/game.
- `VodRepository`
  `vod_files` and `vod_bookmarks`.
- `GameEventsRepository` and `DerivedEventsRepository`
  Live timeline events and derived event instances.

### Tables that matter most

These are the tables future sessions will touch most often:

- `games`
  Core post-game record. Champion, result, KDA, stats, review fields, enemy lane, etc.
- `session_log`
  Per-game session context like mental rating, improvement note, `rule_broken`, mood.
- `objectives`
  Learning objectives plus score/game-count state.
- `game_objectives`
  Per-game objective practice tracking.
- `rules`
  User rules.
- `matchup_notes`
  Champion vs enemy note storage.
- `vod_files`
  Linked VOD path per game.
- `vod_bookmarks`
  Timestamped bookmark / clip metadata per game.
- `game_events`
  Raw event timeline.
- `derived_event_instances`
  Computed higher-level event windows.

## Core Game Data Flow

### Normal game-end path

This is the happy path:

1. `GameMonitorService` polls the LCU gameflow phase.
2. When the game ends, it tries `GetEndOfGameStatsAsync`.
3. `StatsExtractor.ExtractFromEog` converts raw JSON into `GameStats`.
4. `GameEndedMessage` is sent through the messenger.
5. `GameService.ProcessGameEndAsync` persists the game, session log, events, derived events, and tries VOD linking.
6. Review UI loads that stored game through `ReviewViewModel`.

### Missed-game reconciliation path

This is the fallback when end-of-game stats were missed.

1. `GameMonitorService` marks reconciliation pending.
2. It later calls `ILcuClient.GetMatchHistoryAsync`.
3. `StatsExtractor.ExtractFromMatchHistory` builds `GameStats` from match history.
4. The monitor backfills any recently-missed games not already in the DB.

Important limitation:

- match-history payloads are weaker than the EOG payload
- this path has historically caused missing names / weaker metadata
- recent local source changes added champion-name resolution by `championId` through LCU assets to reduce `Unknown` rows

## Review Flow

The review path is mostly centered around `ReviewViewModel.cs`.

What it loads/saves:

- main review text
- mistakes / went well / focus next
- session note and mental reflection
- concept tags
- objective practice state
- matchup note for current champion vs enemy
- matchup note history for the same pairing

Key persistence boundary:

- `ReviewViewModel` prepares user-facing state
- `GameRepository.UpdateReviewAsync` writes the core review fields to `games`
- supporting repositories persist notes/tags/objective practice

If review behavior seems wrong, check these first:

- `ReviewViewModel.cs`
- `GameRepository.cs`
- `MatchupNotesRepository.cs`
- `SessionLogRepository.cs`

## VOD Flow

Main files:

- `VodService.cs`
- `VodRepository.cs`
- `VodPlayerViewModel.cs`
- `SettingsViewModel.cs`

How it works:

- user config points at an Ascent recordings folder
- after a game, `GameService` asks `VodService.TryLinkRecordingAsync`
- if the first attempt misses, `GameService` schedules a delayed retry
- matched files are stored in `vod_files`
- bookmarks/clips are stored in `vod_bookmarks`

Important reality:

- the DB stores VOD file paths, not the video file bytes
- VOD reliability depends on timestamp matching and valid Ascent folder config

## Updater / Release Flow

Main files:

- `src/Revu.App/Program.cs`
- `src/Revu.App/Services/UpdateService.cs`
- `src/Revu.App/ViewModels/SettingsViewModel.cs`
- `.github/workflows/release.yml`

Behavior:

- installed app only, not debug builds, can auto-update
- Velopack hooks run in `Program.cs`
- `UpdateService` checks GitHub releases via `GithubSource`
- the release workflow stamps `<Version>` from the pushed tag, publishes a self-contained build, packs with `vpk`, and uploads release assets

Important recent history:

- `v2.3.4` was released as a hotfix for updater progress-threading in `SettingsViewModel`
- the installed-app updater previously failed when progress UI was updated from a non-UI thread

## LCU Integration

Main files:

- `LcuCredentialDiscovery.cs`
- `LcuClient.cs`
- `GameMonitorService.cs`
- `LiveEventApi.cs`
- `LiveEventCollector.cs`
- `StatsExtractor.cs`

Responsibilities:

- discover client lockfile credentials
- configure authenticated HTTPS calls to the local LCU
- poll gameflow
- fetch EOG stats and match history
- collect live event API events during games
- transform raw payloads into `GameStats`

Known nuances:

- the League client uses a self-signed cert, so the app intentionally bypasses certificate validation for these local calls
- Riot payload shapes are not stable; backfill logic has already needed updates for nested match-history payloads and 64-bit game IDs

## Analytics / Dashboard

Main files:

- `DashboardViewModel.cs`
- `HistoryViewModel.cs`
- `LossesViewModel.cs`
- `AnalyticsViewModel.cs`
- repository aggregate methods in `GameRepository.cs` and `SessionLogRepository.cs`

Notable behavior:

- adherence streak comes from `SessionLogRepository.GetAdherenceStreakAsync()`
- it counts descending session-log dates until the first date with `SUM(rule_broken) > 0`
- this is date-grouped session data, not a separate dedicated streak table

## Build Notes

Normal local build on this machine:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' `
  'src\Revu.App\Revu.App.csproj' `
  /t:Build `
  /p:Configuration=Debug `
  /p:Platform=x64 `
  /p:RuntimeIdentifier=win-x64 `
  /m `
  /verbosity:minimal
```

Important machine-specific note:

- on this machine, `dotnet`-driven WinUI packaging/publish paths have hit a missing `Microsoft.Build.Packaging.Pri.Tasks.dll`
- Visual Studio MSBuild works reliably and should be preferred for local builds here

## What To Back Up Before Manual Data Surgery

Future sessions should be conservative with the live DB. Before editing user data manually:

1. close the installed app
2. copy `%LOCALAPPDATA%\\LoLReviewData\\revu.db`
3. then make the change
4. run `PRAGMA integrity_check`
5. relaunch the app

This has been necessary multiple times in live support work.

## AI Coaching Rebuild (2026-04-18)

The prior "Coach Lab" system (Gemma-only clip-first coaching, hidden page
behind `LOLREVIEW_ENABLE_COACH_LAB`, persistent Python worker, training
pipeline) was removed in coach phase -1. A new coaching architecture is
being built per `COACH_PLAN.md`.

**Cleaned up:**
- All `Coach*` and `ICoach*` services in `src/Revu.Core/Services/`
- `CoachLabViewModel.cs`, `CoachLabPage.xaml[.cs]`
- `CoachLabModels.cs`
- Coach-specific tests (CoachLabServiceTests, CoachTrainingStatusTests,
  cascade-delete test, requeue tests)
- `experiments/coach_lab/` (full Gemma Python stack + training scripts)
- `.venv-coach-gemma/` virtualenv
- DI registrations, nav entry, sidebar button, VOD bookmark sync hook

**Intentionally kept:**
- 8 `coach_*` DB tables remain in `Schema.AllMigrations` (CreateCoachPlayers,
  CreateCoachMoments, CreateCoachLabels, CreateCoachInferences,
  CreateCoachRecommendations, CreateCoachModels, CreateCoachDatasetVersions,
  CreateCoachObjectiveBlocks) + 3 migration arrays. Dropping them would
  corrupt existing user DBs. No code path touches them now — they're
  orphaned but harmless.
- `experiments/sam3_vod/` (unrelated VOD analysis utility)

**Audit artifact:** `COACH_CLEANUP_AUDIT.md` at repo root.
**Plan:** `COACH_PLAN.md` at repo root.
**Live state:** `COACH_STATUS.md` at repo root.

**Current phase status:** Phase -1 complete. Phase 0+ coming next in the
same session, directly to `main` (no per-phase branches).

## Current Local State As Of 2026-03-28

Released commits:

- `82ce8c1` `feat: ship review flow and VOD improvements`
- `ab762fe` `fix: hotfix updater progress threading`

Current uncommitted source changes:

- `src/Revu.Core/Lcu/ILcuClient.cs`
- `src/Revu.Core/Lcu/LcuClient.cs`
- `src/Revu.Core/Lcu/GameMonitorService.cs`

Those local edits add champion-name resolution for the match-history reconciliation path so future missed-game backfills do not save `Unknown` when only `championId` is present.

Important local user-data context:

- the live DB on this machine has already had manual cleanup / repair work
- do not assume the user DB state is a pristine reflection of source migrations alone
- if a future session is debugging "why does this row look strange?", check whether it may have been manually corrected in the live DB

## Suggested Debugging Entry Points

If the issue is:

- app startup / crash
  start with `Program.cs`, `App.xaml.cs`, `startup.log`, `crash.log`
- data missing after reinstall
  start with `AppDataPaths.cs`, `LegacyDatabaseMigrationService.cs`, `DatabaseInitializer.cs`
- game not captured
  start with `GameMonitorService.cs`, `LcuClient.cs`, `StatsExtractor.cs`, `GameService.cs`
- weird game row / `Unknown` names
  start with `StatsExtractor.cs`, `GameMonitorService.cs`, and inspect the actual DB row
- review did not persist
  start with `ReviewViewModel.cs`, `GameRepository.UpdateReviewAsync`, `SessionLogRepository`
- VOD did not attach
  start with `VodService.cs`, `VodRepository.cs`, config `AscentFolder`, and the live DB `vod_files` rows
- updater broken
  start with `UpdateService.cs`, `SettingsViewModel.cs`, `velopack.log`, and `.github/workflows/release.yml`

## Final Guidance

This codebase is not huge, but it is stateful:

- app state is split between UI, SQLite, and the live League client
- many bugs are not pure code bugs; they are migration, payload-shape, or local-data-shape issues
- the fastest path in most sessions is usually:
  read the repo map
  inspect the relevant DB rows
  inspect the relevant repository or LCU parser
  only then change code

If a future session starts here, the safest first move is to read this file, then run `git status`, then inspect the live DB before making assumptions.

# Revu - Future Work Implementation Backlog

This document captures codebase-level work that should be implemented in
future sessions. It is intentionally practical: each item names the problem,
why it matters, where to start, and what "done" means.

Use this when starting future work that is larger than a bug fix. Pull from
the top unless there is a live user issue that should override it.

---

## Working Rules For Future Sessions

- Preserve the current App/Core split. `Revu.App` owns UI composition and
  desktop lifecycle; `Revu.Core` owns persistence, LCU parsing, repositories,
  and domain services.
- Treat `%LOCALAPPDATA%\LoLReviewData\revu.db` as user-owned data. Before any
  migration or data repair, create a backup and run integrity checks.
- Prefer narrow, verified changes over broad cleanup. This repo has several
  stateful paths where "obvious" refactors can change behavior.
- Keep release compatibility in mind. `LoLReview` and `LoLReviewData` names
  are historical but important for Velopack and existing installs.

---

## Deferred: Normal Pull Request CI

**Signal.** The release workflow runs tests before publishing, but there is no
ordinary PR/push CI workflow. That means broken builds can sit on `main` until
release time.

**Decision.** Deferred by product preference. For now, CI should only run on
tag/manual release through `.github/workflows/release.yml`. Do not add a normal
`ci.yml` unless that preference changes.

**Revisit when.**

- External contributors start opening PRs.
- Main branch breakage becomes a recurring issue.
- Release-day failures become expensive enough to justify earlier automation.

---

## Priority 1: Protect Credential-Like Config Values

**Status.** Implemented on `gpt-work`: `ConfigService` now migrates
`RiotSessionToken` and `GithubToken` into DPAPI-backed protected storage,
hydrates them for existing callers, and writes sanitized `config.json`.

**Signal.** Coach provider API keys use DPAPI through `CoachCredentialStore`,
but `RiotSessionToken` and `GithubToken` are stored on `AppConfig`, which is
serialized to plaintext `config.json`.

**Why it matters.** The app has a strong local-first privacy story. Plaintext
session tokens are inconsistent with that story and create avoidable support
risk if users share config files while debugging.

**Start here.**

- `src/Revu.Core/Models/AppConfig.cs`
- `src/Revu.Core/Services/ConfigService.cs`
- `src/Revu.App/Services/CoachCredentialStore.cs`
- `src/Revu.App/ViewModels/SettingsViewModel.cs`
- `src/Revu.App/ViewModels/OnboardingViewModel.cs`
- `src/Revu.App/ViewModels/ShellViewModel.cs`
- `src/Revu.Core/Services/RiotMatchClient.cs`

**Implementation sketch.**

1. Create a generic protected secret store in `Revu.Core` or an app-level
   service that can store named secrets with DPAPI CurrentUser scope.
2. Move Riot session token storage out of `AppConfig`.
3. Keep non-secret session metadata in config: email, expiry, Riot ID, region,
   PUUID, primary role.
4. Migrate existing plaintext tokens on startup: read once from config, write
   to protected storage, clear the config field.
5. Decide whether `GithubToken` is still needed. If it is, move it too.

**Done means.**

- Newly saved config files do not contain bearer/session tokens.
- Existing users keep their sessions after upgrade.
- Logout clears protected tokens and config metadata.
- Unit tests cover migration from plaintext config to protected storage.

---

## Priority 2: Split The Largest ViewModels Into Focused Services

**Status.** Implemented on `gpt-work`: VOD bookmark mutation queuing was
extracted into `SerializedTaskQueue` with failure-isolation tests, and
dashboard/history/analytics consumers now depend on narrower game query
interfaces instead of the full repository facade.

**Signal.** Several classes are large enough that future edits will become
risky:

- `VodPlayerViewModel.cs` is about 1430 lines.
- `SettingsViewModel.cs` is about 924 lines.
- `DashboardViewModel.cs` is about 853 lines.
- `ReviewViewModel.cs` is about 828 lines.

**Why it matters.** These classes are stateful UI coordinators. Large
viewmodels make regressions more likely because unrelated behavior lives in
one mutation surface.

**Start here.**

- `src/Revu.App/ViewModels/VodPlayerViewModel.cs`
- `src/Revu.App/ViewModels/SettingsViewModel.cs`
- `src/Revu.App/ViewModels/DashboardViewModel.cs`
- `src/Revu.App/ViewModels/ReviewViewModel.cs`
- `src/Revu.Core/Services/ReviewWorkflowService.cs`

**Implementation sketch.**

1. Do not start with a rewrite. Pick one viewmodel per session.
2. Extract behavior already shaped like a service:
   - VOD bookmark mutation queue
   - clip creation and tagging
   - settings backup/restore orchestration
   - update diagnostics
   - dashboard card data assembly
3. Keep observable UI state in the viewmodel.
4. Move persistence orchestration into services with small tests.

**Done means.**

- Each extracted service has focused tests or at least an isolated fake-backed
  test around failure handling.
- Viewmodel public binding surface stays stable.
- No UI strings or XAML bindings break.
- The app build and Core tests pass.

---

## Priority 3: Break Up Repository Query Objects

**Status.** Implemented on `gpt-work`: `GameRepository` was split into core,
history, deletion, and analytics partial files. New `IGameHistoryQuery`,
`IGameAnalyticsQuery`, and `IGameDeletionService` interfaces are registered in
DI, and moved query paths have regression tests.

**Signal.** `GameRepository.cs` is about 1100 lines and mixes writes,
history queries, analytics aggregates, matchup stats, chart data, and delete
side effects.

**Why it matters.** Repository bloat makes schema work harder. Small query
objects are easier to test and less likely to break unrelated screens.

**Start here.**

- `src/Revu.Core/Data/Repositories/GameRepository.cs`
- `src/Revu.Core/Data/Repositories/IGameRepository.cs`
- `src/Revu.Core/Services/AnalysisService.cs`
- `src/Revu.App/ViewModels/DashboardViewModel.cs`
- `src/Revu.App/ViewModels/AnalyticsViewModel.cs`

**Implementation sketch.**

1. Keep `IGameRepository` for core game CRUD and review persistence.
2. Introduce focused interfaces for read models, for example:
   - `IGameHistoryQuery`
   - `IGameAnalyticsQuery`
   - `IGameDeletionService`
3. Move SQL in small batches without changing query text unless tests require
   it.
4. Add regression tests for moved queries using `TestDatabaseScope`.

**Done means.**

- `GameRepository` is materially smaller and still owns core writes.
- Analytics/history consumers depend on narrower interfaces.
- Existing Core tests pass, and at least two moved query paths have tests.

---

## Priority 4: Make Async Fire-And-Forget Paths Observable

**Status.** Implemented on `gpt-work`: added `BackgroundTaskRunner`, replaced
important raw fire-and-forget notifiers/startup checks with logged execution,
and tied the review VOD retry path to its cancellation token.

**Signal.** The codebase uses many intentional fire-and-forget operations:
VOD retry, coach notifications, startup checks, bookmark mutations, sidecar
health polling, and UI dispatcher continuations.

**Why it matters.** Fire-and-forget is valid in a desktop app, but failed
background work must be visible in logs and cancellable when the owning page
or service goes away.

**Start here.**

- `src/Revu.Core/Services/GameService.cs`
- `src/Revu.Core/Services/GameLifecycleWorkflowService.cs`
- `src/Revu.Core/Services/ReviewWorkflowService.cs`
- `src/Revu.App/ViewModels/VodPlayerViewModel.cs`
- `src/Revu.App/ViewModels/ShellViewModel.cs`
- `src/Revu.App/Services/CoachSidecarService.cs`
- `src/Revu.Core/Lcu/GameMonitorService.cs`

**Implementation sketch.**

1. Add a tiny helper for logged background task execution, probably in Core
   and App variants if UI dispatching is involved.
2. Replace raw `_ = SomeAsync()` where the failure path is currently easy to
   miss.
3. Tie long-lived background tasks to an owner `CancellationTokenSource`.
4. Avoid changing UX semantics; this is observability and lifecycle cleanup.

**Done means.**

- No important fire-and-forget task can fault silently.
- Page-owned background work cancels on unload/navigation where practical.
- Logs include enough context to identify game id, bookmark id, or operation.

---

## Priority 5: Add Coach And Proxy Test Suites

**Status.** Implemented on `gpt-work`: added coach pytest coverage for DB
allowlist enforcement, API-key-safe config persistence, and a FastAPI config
route. Added Vitest proxy tests for auth failures, match id validation, region
mapping, and rate limiting. No normal CI workflow was added.

**Signal.** The C# Core has 94 tests. The Python coach sidecar declares
pytest dependencies, and the Cloudflare proxy has TypeScript config, but there
are no committed tests for either.

**Why it matters.** The coach sidecar writes to the user's DB. The proxy owns
auth and Riot API access. Both are high-trust surfaces.

**Start here.**

- `coach/pyproject.toml`
- `coach/coach/db.py`
- `coach/coach/main.py`
- `coach/coach/modes/*.py`
- `proxy/src/index.ts`
- `proxy/src/auth.ts`
- `proxy/src/db.ts`
- `proxy/migrations/0001_init.sql`

**Implementation sketch.**

Coach:

1. Add `coach/tests/`.
2. Test `db.py` allowlist enforcement.
3. Test config loading without leaking API keys.
4. Add at least one FastAPI route test with a temporary SQLite DB.

Proxy:

1. Add a test runner such as Vitest or Miniflare-based worker tests.
2. Test auth failures, rate-limit behavior, region mapping, and match id
   validation.
3. Keep tests offline by mocking Riot fetch calls.

**Done means.**

- `pytest` runs locally for coach core tests.
- Proxy tests run through `npm test`.
- CI has separate non-blocking or blocking jobs for coach/proxy once stable.

---

## Priority 6: Clean Up Build Warnings And Binding Warnings

**Status.** Implemented on `gpt-work`: Debug x64 MSBuild now completes without
WMC1506 warnings. Static/page computed bindings were converted to observable
sources or direct ViewModel bindings.

**Signal.** The local WinUI build succeeds but emits WMC1506 binding warnings
in several XAML files.

**Why it matters.** Binding warnings are often harmless, but they hide real
binding mistakes when the warning list becomes normal noise.

**Start here.**

- `src/Revu.App/Dialogs/ManualEntryDialog.xaml`
- `src/Revu.App/Dialogs/StartBlockDialog.xaml`
- `src/Revu.App/Views/DashboardPage.xaml`
- `src/Revu.App/Views/ReviewPage.xaml`
- `src/Revu.App/Services/FontSizes.cs`

**Implementation sketch.**

1. Review each WMC1506 warning from a clean MSBuild output.
2. For static-ish values such as font sizes, consider exposing immutable
   resources instead of one-way bindings.
3. For x:Bind page properties such as `HasObjectives`, ensure the page raises
   notification or convert to ViewModel bindings.
4. Avoid broad warning suppression unless a binding is intentionally static.

**Done means.**

- Debug x64 MSBuild emits no WMC1506 warnings, or each remaining warning is
  documented and intentionally suppressed.
- No page loses dynamic updates after conversion.

---

## Priority 7: Formalize Schema Versioning

**Status.** Implemented on `gpt-work`: added `schema_metadata`,
`Schema.VersionedMigrations`, metadata-gated migration execution, and tests
that assert the current schema version is recorded.

**Signal.** `Schema.AllMigrations` runs every startup and tolerates duplicate
column errors. `DatabaseInitializer` then normalizes legacy/hybrid tables.

**Why it matters.** This has worked, but as migrations grow, fail-and-swallow
startup migration becomes harder to reason about and slower on old machines.

**Start here.**

- `src/Revu.Core/Data/Schema.cs`
- `src/Revu.Core/Data/DatabaseInitializer.cs`
- `src/Revu.Core.Tests/DatabaseInitializerTests.cs`

**Implementation sketch.**

1. Add a small metadata table, for example `schema_metadata`.
2. Record the latest applied app schema version.
3. Keep legacy normalization paths for existing databases.
4. For new migrations, gate execution by metadata version instead of relying
   only on duplicate-column exceptions.
5. Preserve idempotence. A half-applied migration should remain recoverable.

**Done means.**

- Fresh DB creation and old DB upgrade both pass tests.
- Startup still creates safety backups before migration.
- Existing user databases without metadata migrate correctly.

---

## Priority 8: Long-Soak Performance Harness

**Status.** Implemented on `gpt-work`: added
`scripts/perf-long-soak.ps1` and `docs/PERF_LONG_SOAK.md` with sampling,
manual navigation loop, and pass criteria.

**Signal.** Existing docs mention possible long-run memory growth and deferred
VOD/backfill performance checks.

**Why it matters.** Desktop app memory issues usually appear after navigation,
media open/close cycles, or many hours of use.

**Start here.**

- `docs/PERF_BASELINE.md`
- `docs/V2_17_BACKLOG.md`
- `src/Revu.App/Views/VodPlayerPage.xaml.cs`
- `src/Revu.App/Helpers/SidebarEnergyDrainAnimator.cs`
- `src/Revu.App/Controls/HudProgressRing.xaml.cs`

**Implementation sketch.**

1. Add a script or manual checklist for a 24-hour idle/navigation soak.
2. Sample private working set, handle count, and CPU every 5 minutes.
3. Include a VOD open/close loop.
4. Record results in a new `docs/PERF_LONG_SOAK.md`.

**Done means.**

- There is a repeatable performance check before major releases.
- Results distinguish monotonic leaks from GC/compositor plateaus.
- Any confirmed issue gets a small repro and owner file list.

---

## Priority 9: Revisit UI Theme Resource Strategy

**Status.** Implemented on `gpt-work`: confirmed `WinUIThemeStubs.xaml` was
not referenced, removed it, and documented the current `XamlControlsResources`
plus `AppTheme.xaml` loading strategy in `CODEBASE_ONBOARDING.md`.

**Signal.** `WinUIThemeStubs.xaml` is about 3000 lines and exists because
`XamlControlsResources` previously caused instability in unpackaged mode.
`AppResourcesStartupTask` now attempts to load `XamlControlsResources` at
runtime.

**Why it matters.** Theme resource workarounds are easy to forget and hard to
debug. The current state should be documented and simplified if the original
heap corruption risk is gone.

**Start here.**

- `src/Revu.App/Themes/WinUIThemeStubs.xaml`
- `src/Revu.App/Themes/AppTheme.xaml`
- `src/Revu.App/Startup/AppResourcesStartupTask.cs`
- `src/Revu.App/App.xaml`

**Implementation sketch.**

1. Confirm whether `WinUIThemeStubs.xaml` is still referenced or purely
   historical.
2. Test app launch and common dialogs with and without runtime
   `XamlControlsResources`.
3. If stubs are obsolete, remove them. If still needed, document why in
   `CODEBASE_ONBOARDING.md`.

**Done means.**

- Theme resource loading has one clear strategy.
- Obsolete stubs are removed, or current stubs are justified in docs.
- App launch, dialogs, and controls still render correctly.

---

## Future Work Parking Lot

**Status.** Implemented on `gpt-work`: added an architecture diagram,
repo-level scripts for common build/test/package commands, a prerelease
checklist, fixture-backed `StatsExtractor` tests, and a public known-issues
document. The Riot proxy URL item remains unchanged because there is no
concrete endpoint-rotation requirement yet.

These are useful but lower priority than the items above.

- Add a lightweight architecture diagram to `CODEBASE_ONBOARDING.md`.
- Add repo-level scripts for common commands: test, build, package coach.
- Add a pre-release checklist that includes CI, MSBuild, Core tests, site link
  check, and coach package verification.
- Add more focused tests for `StatsExtractor` using captured Riot/LCU payload
  fixtures.
- Consider moving Riot proxy URL to a signed/release-time config only if there
  is a concrete need to rotate endpoints without rebuilding.
- Add a public known-issues doc after first cohort feedback.

---

## Quick Verification Commands

Use these before and after future work:

```powershell
dotnet test src\Revu.Core.Tests\Revu.Core.Tests.csproj --no-restore -c Debug -p:Platform=x64
```

```powershell
.\scripts\test-coach.ps1
```

```powershell
.\scripts\test-proxy.ps1
```

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

```powershell
git status --short --branch
```

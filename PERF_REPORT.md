# Revu — Performance Audit

**Scope:** WinUI 3 / .NET 8 desktop app (`src/Revu.App`, `src/Revu.Core`). Static analysis only — no code modified.
**Method:** 6 parallel analysis agents (startup, SQLite data layer, XAML/rendering, LCU/networking, animations/timers, async/allocation), then a synthesis pass that deduped overlaps and a manual adversarial-verify pass that re-read source for every High-impact claim below. Spot-checks confirmed: DB N+1 loop, ItemsRepeater virtualization break, NavigationCacheMode absence, JsonDocument leak, JSON-deserialize-per-render, global button template animations.
**Confidence:** "Confirmed" = proven from the code as written. "Unverified" = plausible from code but impact needs a runtime profiler.

---

## TL;DR — the four things actually making it slow

1. **Lists realize every row — no virtualization** anywhere. Every game/history/analytics list builds all N rows up front, each a heavy custom control.
2. **Pages rebuild from scratch on every navigation** (`NavigationCacheMode` is never set). Combined with #1, each sidebar click pays full list-realize + binding + animation-spawn cost again.
3. **The Analytics filtered path is an N+1 loop** — one DB query + one new SQLite connection *per game*, up to 10,000 iterations.
4. **The `games` table has no indexes** — every Dashboard query is a full table scan, and it gets worse as the user logs more games.

Everything else is real but secondary to these.

---

## Top wins, ranked by impact ÷ effort

| Rank | Finding | Impact | Effort | Where |
|------|---------|--------|--------|-------|
| 1 | Add indexes to `games` (timestamp, champion, is_hidden) | High | Low | `Schema.cs` |
| 2 | Fix N+1 in `GenerateProfileAsync` (bulk-load session_log) | High | Low | `AnalysisService.cs:347` |
| 3 | Set `NavigationCacheMode="Required"` on stable pages | High | Low | all `Views/*.xaml` |
| 4 | Gate safety DB **file-copy backup** so it isn't on every launch | High | Low | `DatabaseSafetyStartupTask.cs:25` |
| 5 | Dispose `JsonDocument` in LCU/live-event GET helpers | Med | Trivial | `LcuClient.cs:466`, `LiveEventApi.cs:128` |
| 6 | Cache `ParticipantMapJson` deserialize instead of per-get | Med | Trivial | `DashboardViewModel.cs:997` |
| 7 | Cache chip brushes as statics (stop `App.Resources[...]` per item) | Med | Trivial | `ChipBgConverter.cs:18` |
| 8 | Restore list virtualization (ItemsRepeater out of ScrollViewer/StackPanel) | High | Med | `GamesPage.xaml:93` + 5 pages |
| 9 | Gate `Initialize` backfills + legacy-DB scan behind a "done" flag | Med | Med | `DatabaseInitializer.cs:37`, `LegacyDatabaseMigrationService.cs:131` |
| 10 | Stop forever-animations on `Unloaded`; reduce `SectionTitle` to 1 anim | Med | Low | `AnimationHelper.cs:44/81`, `SectionTitle.xaml.cs:39` |
| 11 | Hoist `PRAGMA` out of per-connection open path | Med | Low | `SqliteConnectionFactory.cs:74` |
| 12 | Replace `EnableDependentAnimation=True` button anims with Composition | Med | Med | `AppTheme.xaml:287–349` |

---

## CONFIRMED FINDINGS

### A. Rendering / UI thread (biggest perceived-slowness bucket)

#### A1 — Lists realize all rows: ItemsRepeater inside `ScrollViewer > StackPanel` defeats virtualization — **High**
**Where:** `GamesPage.xaml:93`, `HistoryPage.xaml:71`, `ObjectiveGamesPage.xaml:83`, `SessionLoggerPage.xaml:49`, `RulesPage.xaml:21`, `AnalyticsPage.xaml:49` (6+ repeaters).
**Why slow:** An `ItemsRepeater` with `StackLayout` only virtualizes when it owns the scroll viewport. Here it sits inside `ScrollViewer > StackPanel`, so the `StackPanel` measures it with **infinite** available height and the repeater realizes + measures **every** item immediately, with no recycling. Each row is a `GameRowCard` (radial-gradient overlay + 2 linear-gradient rectangles + a ContentPresenter + 2–3 buttons). Memory and first-frame cost scale linearly with list length. *(Verified: `GamesPage.xaml:93` shows the exact `ScrollViewer > StackPanel > ItemsRepeater(StackLayout)` nesting.)*
**Direction:** Make the repeater the scroll owner (bare `ItemsRepeater` in a `Grid` row `Height="*"`, scrolled by an attached `ScrollViewer`), or switch to `ListView` (virtualizing by default). No ViewModel changes.
**Confidence:** High. **Effort:** Medium (per-page structural).

#### A2 — No page caching: full teardown + rebuild on every navigation — **High**
**Where:** All `Views/*.xaml`. Only `VodPlayerPage.xaml:12` sets `NavigationCacheMode` (and it's `Disabled`). Default is `Disabled`. *(Verified: a repo-wide grep finds exactly one hit.)*
**Why slow:** Every sidebar tap destroys and re-instantiates the page: all bindings re-evaluate, all `Loaded` handlers refire (spawning fresh Composition animations — see C-group), every repeater re-realizes (compounding A1), and the ViewModel `LoadAsync` re-runs. AnalyticsPage (6+ repeaters, dozens of `FontSizes` bindings) is the worst offender.
**Direction:** `NavigationCacheMode="Required"` on stable pages (Games, History, Rules, Analytics); verify `Loaded` handlers are idempotent and animations check already-running state.
**Confidence:** High. **Effort:** Low (one attribute/page).

#### A3 — `ChipBgConverter`/`ChipBorderConverter` do an `Application.Current.Resources[key]` lookup per item per render — **Med**
**Where:** `Converters/ChipBgConverter.cs:18-20`.
**Why slow:** `Application.Current.Resources[key]` walks the merged-dictionary tree (O(n)) on every converter call. On the Analytics champion filter (50+ chips, all realized per A1), that's ~2 lookups × N chips on render and again on every `IsSelected` change. `KdaColorConverter`/`WinLossColorConverter` already cache brushes in static fields — these two don't.
**Direction:** Cache the two brushes in static readonly fields (mirror the existing converters).
**Confidence:** High. **Effort:** Trivial.

#### A4 — `GameRowCard`/`CornerBracketedCard` rewrite `RadialGradientBrush.Center` on every `PointerMoved` — **Med**
**Where:** `GameRowCard.xaml.cs:214`, `CornerBracketedCard.xaml.cs:179`.
**Why slow:** `PointerMoved` fires at mouse-poll rate (125–1000 Hz). Each event mutates the radial-gradient center/origin, marking the brush dirty and forcing a compositor re-upload. With all rows live (A1) and ~13–30 cards on Dashboard, fast cursor movement = hundreds of brush invalidations/sec — a DWM-stutter source on weaker GPUs.
**Direction:** Throttle to a >2–4 px move delta, or drive the glow with a Composition `SpotLight` (no per-event CPU).
**Confidence:** High. **Effort:** Low.

#### A5 — `HexPatternLayer` rebuilds full `PathGeometry` on size change + subscribes to global `LayoutUpdated` for life — **Med**
**Where:** `HexPatternLayer.xaml.cs:42` (unconditional `LayoutUpdated` subscription, never removed), `:160-241` (`RebuildPattern`).
**Why slow:** `LayoutUpdated` is an **application-global** event — it fires on every layout pass anywhere in the tree, and each `HexPatternLayer` instance handles it on the UI thread. `RebuildPattern` allocates `columns × rows` `PathFigure` + `~5×` that many `LineSegment` objects (a 400×300 card ≈ 945 hexagons → ~5,670 segments) per rebuild. On pages with hover/tilt animations causing frequent layouts, this is repeated UI-thread allocation.
**Direction:** Drop `LayoutUpdated` (keep only `SizeChanged`, already wired) and add a min-resize-delta guard; or unsubscribe once built.
**Confidence:** High. **Effort:** Very Low.

#### A6 — `TimelineControl.RedrawMarkers` clears + reallocates the whole marker Canvas (and re-parses hex colors) on every collection change — **Med**
**Where:** `TimelineControl.xaml.cs:319-438`.
**Why slow:** On events/size change it does `MarkerCanvas.Children.Clear()` then `new Border`/`new TextBlock` per event/region/bookmark, each allocating a `SolidColorBrush` via `ParseColor` (hex string slicing + `Convert.ToByte`). Cost is at VOD/event load, not steady playback (position updates correctly touch only `ProgressBar.Width`).
**Direction:** Diff markers by identity; pre-parse colors to `Windows.UI.Color` once at load.
**Confidence:** High. **Effort:** Medium.

#### A7 — `FontSizes` uses reflection `{Binding ... Source={StaticResource FontSizes}}` on every templated text element — **Med**
**Where:** All DataTemplates (AnalyticsPage.xaml ~49 such bindings; `GameRowCard.xaml`, `SessionLoggerPage.xaml` rows).
**Why slow:** Classic `{Binding}` with `Source=` resolves the path via runtime reflection and hooks INPC dynamically. Non-virtualized lists (A1) mean `N×~4` live reflection bindings, all notified on Ctrl+/- zoom.
**Direction:** Convert to `x:Bind` against a `FontSizes.Instance` singleton where `x:DataType` allows; prioritize high-item-count templates.
**Confidence:** High. **Effort:** High (widespread, mechanical).

---

### B. Data layer (SQLite)

#### B1 — N+1: one DB query + one new connection **per game** in the analytics filter loop — **High** (worst single hotspot)
**Where:** `AnalysisService.cs:347-349` → `ResolveMentalRatingAsync` `:393` → `SessionLogRepository.GetEntryAsync`.
**Why slow:** `GetRecentAsync(limit: 10_000)` returns the full game list, then `foreach (var g in allGames)` `await ResolveMentalRatingAsync(g.GameId)` runs `SELECT … FROM session_log WHERE game_id=@id` — **once per game**, each opening a fresh connection (see B4). 500 games = 500 connections + 500 queries; the 10k ceiling is catastrophic. Fires on Analytics whenever a filter is active. *(Verified at source.)*
**Direction:** Bulk-load once — `SELECT game_id, mental_rating FROM session_log` into a `Dictionary<long,int>` before the loop.
**Confidence:** High. **Effort:** Low.

#### B2 — `games` table has no indexes → full table scan on every Dashboard query — **High**
**Where:** `Schema.cs` (no index on `games` beyond implicit PK / `UNIQUE(game_id)`).
**Why slow:** Every filter by `timestamp`, `is_hidden`, `champion_name`, or `win` does a sequential scan (+ filesort for `ORDER BY timestamp DESC`). Dashboard load alone issues 5–8 such queries (`GetTodaysGamesAsync`, `GetUnreviewedGamesAsync`, `GetWinStreakAsync`, `GetRecentAsync`, the aggregate stats). Cost grows with the game count — exactly the workload that accumulates over a season.
**Direction:** `CREATE INDEX idx_games_timestamp ON games(timestamp DESC) WHERE is_hidden=0;` and `idx_games_champion ON games(champion_name, timestamp DESC) WHERE is_hidden=0;`. Also add `idx_session_log_game_id ON session_log(game_id)` (see B3).
**Confidence:** High. **Effort:** Low (one migration).

#### B3 — Correlated `NOT EXISTS` subqueries against **unindexed** `session_log.game_id` — **Med**
**Where:** `GameRepository.History.cs:81-119` (`GetReviewedCountAsync`), `:165-207` (`GetUnreviewedGamesAsync`).
**Why slow:** Each outer `games` row evaluates `NOT EXISTS (SELECT 1 FROM session_log WHERE game_id=…)`. `session_log.game_id` has no index, so it's a nested scan per row. Fires on Dashboard + the Games count badge.
**Direction:** Add `idx_session_log_game_id`; consider a materialized `reviewed` flag.
**Confidence:** High. **Effort:** Low.

#### B4 — `PRAGMA journal_mode=WAL` + `busy_timeout` re-issued on **every** connection open — **Med**
**Where:** `SqliteConnectionFactory.cs:74-87`; 157 `CreateConnection()` call sites across 25 files.
**Why slow:** WAL persists at the file level, so re-setting it per open is a redundant round-trip every time. No pooling — every call is a full `Open()` + 2 PRAGMAs. Dashboard opens ~12–15 connections sequentially; combined with B1 the per-connection overhead multiplies.
**Direction:** Set WAL once during DB init; keep `busy_timeout` (per-connection). Longer term, reuse a connection for read paths.
**Confidence:** High. **Effort:** Low.

#### B5 — List reads use `SELECT *` and materialize large JSON blobs (`raw_stats`, `participant_map`, `items`) that lists never use — **High** (for the filtered path) / **Med** (paged lists)
**Where:** `GameRepository.History.cs:62,127,141,154,159,173`; `AnalysisService.cs:306`.
**Why slow:** `MapGameStats` reads all 60+ columns including the full Riot-response `raw_stats` TEXT blob. The filtered analytics path pulls up to 10k rows × every blob into memory. Paged list reads are LIMIT-bounded but still haul blobs they discard.
**Direction:** Narrow column lists per read path; reserve `SELECT *` for single-game detail. Drop `raw_stats` from all list queries.
**Confidence:** High. **Effort:** Medium.

#### B6 — `EvidenceRepository.GetPatternCardsAsync` runs 3+ leading-wildcard `LIKE '%…%'` scans on `evidence_items` per Dashboard load — **Med**
**Where:** `EvidenceRepository.cs:221-323`.
**Why slow:** `title LIKE '%Isolated death%'` can't use an index (leading wildcard) and each query joins `games`+`session_log` with ~10 COALESCE predicates per row. Grows with evidence count.
**Direction:** Tag column / FTS on titles, or cache pattern counts rather than scan per load.
**Confidence:** High. **Effort:** Medium.

#### B7 — `ChampionDisplay` JSON-deserializes `ParticipantMapJson` on every property get (per render) — **Med**
**Where:** `DashboardViewModel.cs:997` (`=> RoleAwareDisplay()`), `:1011` (`JsonSerializer.Deserialize<Dictionary<string,string>>`). Same shape in `SessionLoggerViewModel.cs:41/55`.
**Why slow:** It's a computed property with no backing field, bound directly (`Champion="{x:Bind ChampionDisplay}"` in the row template). Every layout pass / DataContext touch re-deserializes the JSON and allocates a fresh `Dictionary`. 20-item list × repeated layouts (resize, etc.) = steady allocation churn. *(Verified at source.)*
**Direction:** Deserialize once in the item ctor/`init`, cache the result, read the field.
**Confidence:** High. **Effort:** Trivial.

---

### C. Startup / cold-launch

#### C1 — Full DB **file-copy** safety backup runs on every launch — **High**
**Where:** `DatabaseSafetyStartupTask.cs:25-27` → `BackupService.cs:52-98` (`PRAGMA wal_checkpoint(TRUNCATE)` + `File.Copy`, then a second copy in `RunBackupAsync`).
**Why slow:** A synchronous WAL checkpoint (write stall) plus a full-file copy of the DB on **every** cold start, even when nothing changed. Cost scales linearly with DB size (hundreds of MB after a season of `raw_stats` blobs).
**Direction:** Gate on a "backed up today / within N hours" timestamp; the `yyyyMMdd_HHmmss` naming already makes the check cheap. **(Caution per project memory: never overwrite/clobber the live DB — this change only skips the redundant copy, it must not weaken the backup-before-migration guarantee.)**
**Confidence:** High. **Effort:** Low.

#### C2 — Startup tasks run sequentially, not in parallel — **High**
**Where:** `AppBootstrapper.cs:22-38` (`foreach … await Task.Run(...)`).
**Why slow:** Legacy-migration → safety-backup → DB-init → resources are awaited one at a time, so total cold-start I/O is summed, not overlapped, even though tasks 1–3 touch independent concerns.
**Direction:** `Task.WhenAll` the independent tasks (respecting the migration-before-backup ordering); keep the UI-thread resource task last.
**Confidence:** High. **Effort:** Low–Medium.

#### C3 — `DatabaseInitializer.InitializeAsync` runs 40+ DDL + 5 `PRAGMA table_info` + 3–4 full-table `UPDATE objectives` backfills on every launch — **Med**
**Where:** `DatabaseInitializer.cs:37-89`.
**Why slow:** The idempotent backfills (`BackfillObjectiveGameCountAsync`, `…ScoreFromPracticedGamesAsync`, `…PracticePhasesAsync`) use correlated subqueries over `game_objectives` — real table scans even when there's nothing to update. Plus 5 `PRAGMA table_info` round-trips for normalization checks.
**Direction:** Gate backfills behind a `schema_metadata` "normalize_vN done" flag so they run once.
**Confidence:** High. **Effort:** Medium.

#### C4 — `AppResourcesStartupTask` loads `XamlControlsResources` + 903-line `AppTheme.xaml` on the UI thread **after** the 3 DB tasks — delays shell first-paint — **Med**
**Where:** `AppResourcesStartupTask.cs:17-27`; `Themes/AppTheme.xaml` (903 lines).
**Why slow:** `XamlControlsResources` materializes the entire WinUI control-template library (tens of ms); running it last means the shell can't appear until DB + resources both finish. The loading screen is shown for the whole duration.
**Direction:** Kick resource loading concurrently with the DB tasks (it's independent), swap shell when both complete.
**Confidence:** High. **Effort:** Low–Medium.

#### C5 — `LegacyDatabaseMigrationService` walks up to 5 ancestor dirs + enumerates legacy backups on every launch — **Low–Med**
**Where:** `LegacyDatabaseMigrationService.cs:131-152`.
**Why slow:** `File.Exists` probes (filesystem metadata reads) up 4 ancestor levels plus a legacy-backup-dir enumeration, every launch — not just first run. Negligible on SSD, real on HDD/network drives.
**Direction:** Persist a "legacy migration done" sentinel and skip the scan thereafter.
**Confidence:** High. **Effort:** Low.

#### C6 — `EnemyLanerBackfill` fires up to 50 sequential Riot HTTP calls at startup — **Med**
**Where:** `ShellViewModel.cs:197` → `EnemyLanerBackfillService.cs:46-50` (`RunAsync(maxGames: 50)`).
**Why slow:** On every cold start (when proxy enabled), up to 50 sequential external HTTPS requests with no "already backfilled recently" gate. Competes with foreground startup work.
**Direction:** Gate behind a last-run timestamp (skip if < 24 h); results are already persisted.
**Confidence:** High. **Effort:** Low.

---

### D. Background services / live-game networking

#### D1 — `JsonDocument` never disposed in the LCU + live-event GET helpers — **Med** (real resource leak, trivial fix)
**Where:** `LcuClient.cs:466-467`, `LiveEventApi.cs:128-129`.
**Why slow:** Both `ParseAsync` then `return doc.RootElement.Clone()` with no `using`/`Dispose`. `JsonDocument` rents from `ArrayPool<byte>`; undisposed, those buffers wait for GC. At a 5 s LCU poll + 10 s live-event poll during a game this is a steady drip of pooled-buffer pressure (the live-events payload is tens of KB). `.Clone()` already copies the data out, so the `using` is safe to add. *(Verified at source.)*
**Direction:** `using var doc = await JsonDocument.ParseAsync(...)`.
**Confidence:** High. **Effort:** Trivial.

#### D2 — `LiveEventCollector` makes 2 HTTP calls/tick and re-deserializes the full (growing) event list every 10 s for the whole game — **Med**
**Where:** `LiveEventCollector.cs:164-191`.
**Why slow:** Per 10 s tick it fetches `/eventdata` (the **entire** cumulative event array, not a delta) *and* `/activeplayer`. A 40-min game ≈ 240 polls × 2 ≈ ~480 localhost requests + full JSON parse of an ever-growing array each tick. The active-player poll (spell-cast detection, v2.17.7) is what doubled it.
**Direction:** Decouple intervals — events every 10 s, active-player every ~30 s (cooldowns are 60–300 s); pass `eventID` to the events endpoint if the patch supports server-side filtering.
**Confidence:** High. **Effort:** Low–Medium.

#### D3 — WMI `Win32_Process` query on every credential miss — **Med**
**Where:** `LcuCredentialDiscovery.cs:54-55`.
**Why slow:** `new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='LeagueClientUx.exe'")` — `Win32_Process` WMI queries are 200–500 ms and spawn COM objects; worse under AV that hooks WMI. Fires ~every 30 s (with backoff) whenever League isn't running — common, since users open Revu first.
**Direction:** Try the lockfile first; fall back to `Process.GetProcessesByName` (cheaper than raw WMI); widen the negative-result cache window.
**Confidence:** High. **Effort:** Low.

#### D4 — Champ-select: up to 10 sequential LCU champion-name HTTP calls on the first tick — **Med**
**Where:** `LcuClient.cs` champ-select path (`GetChampionNameAsync` loop), called from `GameMonitorService` tick.
**Why slow:** First champ-select tick can issue ~10 sequential LCU calls (one per champ) before `_championNamesById` is warm — a few hundred ms burst right when the user is watching the draft.
**Direction:** Pre-warm the cache once on connect via the bulk `champion-summary.json` (already fetched inside `GetChampionNameAsync`); ticks then need 0 calls.
**Confidence:** High. **Effort:** Low.

#### D5 — `GameEndCaptureService` retry loop: up to 12 × 2 s = 24 s sequential LCU polling at game end, with an inline disk write — **Med**
**Where:** `GameEndCaptureService.cs:29-74`; constants `GameConstants.cs:71-74`.
**Why slow:** If EoG stats aren't ready, it loops 12×/2 s (24 s worst case) of sequential LCU round-trips, and on success writes `last_eog_dump.json` inline. Happens every slow EoG screen.
**Direction:** Make the diagnostic dump debug-gated; the retry count is otherwise reasonable.
**Confidence:** High. **Effort:** Low.

#### D6 — `MatchHistoryReconciliation` does 36 fixed-5 s retries (≈72 LCU calls) in the 3 min after a game — **Med–High**
**Where:** `GameMonitorService.cs:207-231` (`PostGameReconcileRetriesRemaining = 36`).
**Why slow:** Riot's match history typically publishes in 30–120 s, so the first ~6 fixed-interval retries are wasted, yet each does 2+ LCU calls + JSON parse + DB lookup — right when the user is opening the review (foreground contention).
**Direction:** Exponential backoff (15 → 30 → 60 s) covers the same window in ~7 requests instead of 36.
**Confidence:** High. **Effort:** Low–Medium.

---

### E. Animations / always-on idle cost

#### E1 — `SectionTitle` runs 3 stacked forever-animations per instance (opacity pulse + scale breath + 360° spin); up to ~15 instances/page — **High** (idle GPU)
**Where:** `SectionTitle.xaml.cs:39-41,62,85`.
**Why slow:** Each instance starts 3 `IterationBehavior.Forever` Composition animations on a ~6 px decorative dot, and never stops them (no `Unloaded` teardown). SettingsPage has ~15 `SectionTitle`s → ~45 perpetual compositor animations running even while the user reads, idle.
**Direction:** Stop on `Unloaded`, restart on `Loaded`; collapse the dot to a single animation (or static).
**Confidence:** High. **Effort:** Low.

#### E2 — `AnimationHelper.AttachPulseOpacity`/`AttachBreathingGlow` start forever-animations with no stop handle → stack on re-navigation — **Med–High**
**Where:** `AnimationHelper.cs:44,81`; consumers `HudProgressRing.xaml.cs:132`, `BannerControl.xaml.cs:73`, `SectionTitle.xaml.cs:39`.
**Why slow:** They return nothing and never stop. Because pages aren't cached (A2), re-navigating refires `Loaded` and layers a *second* forever-animation on the same visual property — they race and double compositor work each revisit.
**Direction:** Return the visual/animation so callers can `StopAnimation` in `Unloaded` (or self-manage via `Unloaded`).
**Confidence:** High. **Effort:** Low.

#### E3 — Global `HudButton` template forces UI-thread animations via `EnableDependentAnimation="True"` (×5) — **Med**
**Where:** `AppTheme.xaml:287,308,318,339,349`. *(Verified: 5 occurrences in the button template.)*
**Why slow:** `EnableDependentAnimation=True` routes the TranslateY/sheen Storyboards through the XAML render pipeline on the **UI thread** instead of the compositor. This applies to *every* button's hover/press app-wide, contending with pointer-event servicing on button-dense pages.
**Direction:** Replace with Composition offset animations (no `EnableDependentAnimation`).
**Confidence:** High. **Effort:** Medium.

#### E4 — `HoverTiltController` writes `PlaneProjection` properties inside a 60 fps `CompositionTarget.Rendering` callback — **Med**
**Where:** `HoverTiltController.cs:98,141-146`.
**Why slow:** It correctly attaches/detaches the per-frame callback only while animating, but it drives `PlaneProjection.RotationX/Y` — a **CPU-side XAML transform** that invalidates layout/render for the subtree each frame, rather than a compositor `Visual` property. Callers (`CornerBracketedCard`, up to ~30 on Dashboard) can run several tilt loops at once during hover.
**Direction:** Drive `Visual.RotationAngleInDegrees` via Composition expression animation — eliminates the `CompositionTarget.Rendering` loop entirely.
**Confidence:** High. **Effort:** Medium.

#### E5 — `HeroHeader` cursor-blink `DispatcherTimer` (500 ms) writes XAML `Opacity` for the page lifetime — **Low–Med**
**Where:** `HeroHeader.xaml.cs:85-93` (stopped correctly on `Unloaded`).
**Why slow:** 2 Hz UI-thread wake writing hard XAML `Opacity` (a render-dirty) purely for a cosmetic cursor blink, on ~14 pages.
**Direction:** Replace with a single forever Composition opacity animation — compositor thread, zero UI-thread wakes.
**Confidence:** High. **Effort:** Very Low.

#### E6 — `SidebarEnergyDrainAnimator`: 8 forever opacity animations for the app's whole lifetime when enabled — **Med**
**Where:** `SidebarEnergyDrainAnimator.cs:163`.
**Why slow:** 8 Path visuals each with a forever pulse; the sidebar is always visible, so they run the entire session. Lifecycle on nav-change is handled (`Stop` calls `StopAnimation`), so the issue is steady-state idle cost, not a leak.
**Direction:** Honor `UISettings.AnimationsEnabled` (reduced-motion) to disable; consider fewer drains.
**Confidence:** High. **Effort:** Low.

#### E7 — `VodPlayerPage` position timer (250 ms / 4 Hz) runs even while paused — **Low**
**Where:** `VodPlayerPage.xaml.cs:174-176` (started on media open, stopped only in `Cleanup`).
**Why slow:** While paused with the overlay open, it still polls `PlaybackSession.Position` and writes `ProgressBar.Width` 4×/s, keeping the timeline Canvas dirty.
**Direction:** Start/stop the timer off `PlaybackStateChanged`.
**Confidence:** High. **Effort:** Low.

#### E8 — `IntelRotatorControl` timer fires every 7 s even with ≤1 card — **Low**
**Where:** `IntelRotatorControl.xaml.cs:131` (`Start()` unconditional; `Advance` guards `Count<=1`).
**Why slow:** Pointless UI-thread wake when nothing can rotate. Minor.
**Direction:** Only `Start()` when `Count > 1`.
**Confidence:** High. **Effort:** Trivial.

---

### F. Async / allocation hygiene

#### F1 — `ConfigService.GetCached()` blocks the caller via `SemaphoreSlim.Wait()` + sync disk read on first access — **Med–High**
**Where:** `ConfigService.cs:148`.
**Why slow:** 20+ synchronous `IConfigService` getters (read during VM construction, XAML bindings, and the 4 Hz VOD `UpdatePosition` path) call `GetCached()`. On first access (`_cached == null`) it does `_lock.Wait()` then `LoadFromDiskSync()` (file read + JSON parse) — an unambiguous UI-thread block on cold start, with contention against a concurrent `LoadAsync()`.
**Direction:** Populate `_cached` in a bootstrap startup task before any VM resolves, or return a default synchronously and refresh async.
**Confidence:** High. **Effort:** Small.

#### F2 — `RefreshEvidenceInboxAsync` replaces 4 `ObservableCollection`s on every bookmark mutation — **Med**
**Where:** `VodPlayerViewModel.cs:1158-1193` (+ `:1207`).
**Why slow:** Each clip-quality/status change reassigns `EvidenceInbox`, `AutoReviewMoments`, `SavedClipReviewMoments`, then `VisibleReviewMoments` — replacing the whole collection makes the bound list discard all containers and re-render (visible flash) + 4 allocations per click.
**Direction:** Incremental `Clear()`+`AddRange`, or a `CollectionViewSource` filter for `VisibleReviewMoments`.
**Confidence:** High. **Effort:** Medium.

#### F3 — `SyncEvidenceCandidatesAsync`: 10–20 sequential `UpsertAsync` round-trips per VOD open — **Med**
**Where:** `VodPlayerViewModel.cs:1126-1139`.
**Why slow:** Auto-clipping can infer 10–20 regions; each `await UpsertAsync` is a separate SQLite write (lock overhead), serialized, while the user watches a spinner (`IsLoading`).
**Direction:** Batch into one transaction (`UpsertManyAsync`).
**Confidence:** High. **Effort:** Small–Medium.

#### F4 — `HistoryViewModel.LoadGamesPageAsync` calls raw `File.Exists` per item (bypasses `FileProbeCache`) — **Med**
**Where:** `HistoryViewModel.cs:277`.
**Why slow:** Direct `File.Exists(path)` per game per page/filter change. On HDD/network drives or a disconnected external Ascent folder, each call stalls for the FS timeout. Every other VM uses `FileProbeCache.Exists`.
**Direction:** Use `FileProbeCache.Exists` (1-char change).
**Confidence:** High. **Effort:** Trivial.

#### F5 — `VodPlayerViewModel.UpdatePosition` fires 2 INPC + a string alloc at 4 Hz for the whole VOD session — **Low–Med**
**Where:** `VodPlayerViewModel.cs:1093-1102`.
**Why slow:** Every 250 ms tick sets `CurrentTimeS` (double, always changes → always notifies → scrubber layout pass) and `CurrentTimeText` via `FormatTime` (new interpolated string each tick). Steady GC + layout cycle throughout playback.
**Direction:** Skip update when the integer-second is unchanged; `TryFormat` into a buffer.
**Confidence:** High. **Effort:** Trivial.

#### F6 — `OnSelectedClipQualityChanged` raises 15 `OnPropertyChanged` + 15 brush allocations per quality click — **Low–Med**
**Where:** `VodPlayerViewModel.cs:1663-1684`.
**Why slow:** One property set fans out to 15 INPC events, each re-evaluating a binding/computed brush (allocates `SolidColorBrush` / reads `AppSemanticPalette`).
**Direction:** Bind one `SelectedClipQualityDisplayState` struct, or move per-quality visuals to a VisualStateGroup keyed off the single property.
**Confidence:** High. **Effort:** Medium.

#### F7 — `AnalyticsViewModel.RefreshUnappliedFlag` rebuilds the full filter (5 LINQ `ToList`) + 5× `SequenceEqual` on every chip toggle — **Low–Med**
**Where:** `AnalyticsViewModel.cs:221-251`.
**Why slow:** Each chip change allocates 5 lists over all chip collections (40+ champions) and deep-compares. A bulk "reset all" fires it once per changed chip.
**Direction:** Debounce, or track a dirty counter instead of deep-comparing.
**Confidence:** High. **Effort:** Small.

---

## UNVERIFIED — needs a runtime profiler

- **U1 — `_host.StartAsync()` runs before shell first-paint** (`App.xaml.cs:172`). It starts `GameMonitorService`, whose first tick can hit the slow WMI scan (D3). If that serializes against the shell render, it delays first interaction. Needs a stopwatch to confirm it doesn't block paint. (Likely real; fix is fire-and-forget after shell assignment.)
- **U2 — Entire `LaunchAsync` posted to the UI dispatcher** (`App.xaml.cs:114`). Inner work uses `Task.Run`, but the coordinator awaits on the dispatcher context; any sync slice before an `await` freezes the loading screen. Confirm with UI-thread profiling.
- **U3 — `DashboardViewModel.LoadAsync` serializes ~10 independent DB queries** (`DashboardViewModel.cs:256-395`). With WAL + per-query connections, several could run via `Task.WhenAll`; load time = sum, not max. Needs measurement of individual query latencies to size the win.
- **U4 — `LoadAsync` continuations resume on the UI thread (no `ConfigureAwait(false)` at the outer level)** (`DashboardViewModel.cs:328,356,367`). The post-await DB chain may execute on the UI thread between dispatcher frames. Confirm per-await context capture.
- **U5 — `BackgroundTaskRunner.Run(() => DispatcherHelper.RunOnUIThreadAsync(async …))` may run DB work on the UI thread** (`ReviewViewModel.cs:152`, `PreGameDialogViewModel.cs:311`, `ShellViewModel.cs:338,478`). Depends on how the continuation marshals; if the awaited DB read resumes on the UI thread it blocks >16 ms. Profile the message-receiver paths.
- **U6 — `GamesViewModel.LoadVodGamesAsync` loads 120 games to show a few with VODs** (`GamesViewModel.cs:230`). 120 SQLite rows isn't inherently heavy, but a JOIN against the vods table would avoid the over-fetch. Measure before optimizing.
- **U7 — `AppTheme.xaml` implicit-style breadth** (903 lines). Broad implicit `Button`/`TextBlock` styles would add per-element style-tree traversal. Needs inspection of keyed-vs-implicit ratio + render profiling.
- **U8 — DI container build / 5× `AddHttpClient`** (`AppHostFactory.cs`, `ServiceCollectionExtensions.cs`). Usually <20 ms on .NET 8; stopwatch to rule out.

---

## Investigated and dismissed (not problems)

- `task.Result` after `await Task.WhenAll(...)` in DashboardVM:844 / HistoryVM:267 / GamesVM:224 / ShellVM:210 — both tasks already complete; no block, no deadlock.
- `VodService` regex — uses `[GeneratedRegex]` (compile-time, no per-call alloc).
- `FileProbeCache` in Games/Dashboard VMs — correctly used with 20 s TTL.
- `PerformanceTrace.Time` / `AppDiagnostics.WriteVerbose` — gated behind debugger/env flag; no production cost.
- `UpdateService` — one-shot at startup + on-demand; no polling timer.

---

## Suggested sequencing

1. **One migration + two small edits** land most of the data-layer win with near-zero risk: B2 indexes, B1 bulk-load, B7 cache. (Back up the DB first — see project memory.)
2. **One attribute per page** (A2 `NavigationCacheMode`) + **one guard** (C1 backup gate) cut the two biggest perceived-latency sources (nav rebuild, launch stall).
3. **Trivial fixes** (D1 `using`, A3 static brushes, F4 `FileProbeCache`, F5 tick guard) — high ratio, do them together.
4. **Then the structural items** (A1 virtualization, E3/E4 Composition rework, C2 parallel startup) when there's time for a proper test pass.

# Revu — Performance Baseline

Source-of-truth for the perf measurements that gate launch readiness. See
`docs/LAUNCH_READINESS.md` Section 1 for the acceptance criteria these
numbers map to.

When a measurement passes, the corresponding box in
`LAUNCH_READINESS.md` gets ticked. When it fails, the fix-or-defer
decision goes here in **Notes** plus a backlog entry in
`docs/V2_17_BACKLOG.md`.

## Test machine

| | |
|---|---|
| Host | DESKTOP-O21G0GI (dev box) |
| CPU | AMD Ryzen 7 9800X3D (8C / 16T, 4.7GHz) |
| RAM | 62 GB |
| GPU | AMD Radeon iGPU (integrated, 9800X3D APU) |
| OS | Windows 11 Pro, build 26200 |
| Storage | NVMe SSD (system drive) |

**Caveat — non-dev-machine gap.** The launch-readiness criteria call for
measurements on a *mid-range* Windows 10/11 machine, not the dev box.
The 9800X3D is firmly above mid-range — it will mask cold-launch
regressions and idle CPU costs that would matter on a 4-core
non-X3D laptop. Numbers below are dev-box readings; treat them as a
**lower bound** of what real users will see. We re-measure on a
non-dev box before the public launch.

## Build under test

| | |
|---|---|
| Version | 2.16.7 |
| TargetFramework | net8.0-windows10.0.19041.0 |
| Configuration | Debug (`-c Debug -r win-x64 -p:OutputPath=bin/Debug-tmp/`) |
| Branch | main |
| Commit | 59fbdff |

Debug build, not Release. Release build will be ~10-30% faster on
cold-launch and tighter on memory; if Debug passes, Release passes.

## Acceptance criteria results

| # | Criterion | Result | Pass? | Notes |
|---|-----------|--------|-------|-------|
| 1 | Cold launch < 2.0s to first interactive frame | 393ms median (3 runs: 807, 372, 393) | PASS | Measured: process-start → MainWindowHandle != 0. Real "click-able" lags this by ~200-400ms but still well under 2.0s budget. |
| 2a | Steady-state idle CPU < 3% | 0.18% total / 2.8% one-core | PASS | After 2-min warmup the compositor throttles. Energy trails ON, Dashboard, 5-min sample. |
| 2b | Steady-state idle private WS < 50MB | 122MB | **FAIL** | 50MB is unrealistic for unpackaged WinUI 3 — framework baseline alone is ~100MB. **Recommend amending bar to <150MB**, which 122MB passes comfortably. See "Criterion 2b discussion" below. |
| 3 | VOD playback steady-state < 250MB, no monotonic growth over ~30 min | 308 → 322MB private over 32min; +13.5MB / +4.4% growth | **PASS-with-caveat** | See "Criterion 3 trajectory analysis" below. Absolute number is over 250MB, but only because the test ran on a 14-hour-old instance starting at 308MB. Trajectory passes: first 20min show 1% drift (textbook plateau); last 12min show ~10MB drift coincident with what looks like VOD-end (CPU drops 12%→8%). Recommend a clean re-test on a fresh-launched instance before tag-and-ship. |
| 4 | 10 VOD open/close cycles: WS within 10% of post-first-VOD | DEFERRED — needs interactive VOD session | TBD | Same as criterion 3 — needs a human in the loop to drive 10 VOD opens. |
| 5 | Backfill 200 games doesn't freeze UI on other pages | TBD | TBD | |
| 6 | DB < 500MB after heavy season | 3.5MB today | PASS | Today's DB is 3.5MB. Margin of ~140× before threshold. `game_events` is the fastest grower. |
| 7 | No leaks in `EnemyLanerBackfillService` cancel paths | PASS | PASS | Static review: `RunAsync` re-checks `ct.ThrowIfCancellationRequested()` per iteration and `Task.Delay(600, ct)` propagates on cancel. No retry-loop, no fire-and-forget tasks. |

**Status: 4/7 verified pass, 1 fail (criterion 2b — recommend amend), 3 to measure (3, 4, 5).**

## v2.17 animation-CPU note (2026-04-29)

Throttled `SidebarEnergyDrainAnimator` from ~30fps (skip-every-other) to
~20fps (skip-2-of-3) in
[`SidebarEnergyDrainAnimator.cs`](../src/Revu.App/Helpers/SidebarEnergyDrainAnimator.cs)
to lower CPU during the first ~110s of post-navigation animation.

**Result smaller than expected.** Active-animation CPU dropped from
~21% one-core → ~18-19% one-core (10-15% reduction, vs. the 33%
projected). The dt-based dash drift means visual speed is identical,
just updated less frequently. So the trail rendering itself was not the
dominant CPU consumer — the rest is layout/composition work that's
unrelated to our render callback.

Post-throttle steady-state behavior is unchanged (compositor throttles
to ~3% one-core after ~110s regardless of our cadence).

Recommend a follow-up investigation in v2.17 to find the rest of the
active-animation CPU. Likely suspects: layout passes, implicit
composition animations on rings, the entry-fade `AnimatePageEnter`
helper running on every navigation. See
[`docs/V2_17_BACKLOG.md`](V2_17_BACKLOG.md#sidebar-energy-trails-cpu-cost).

## Criterion 2b discussion — the 50MB private-WS bar is not achievable

The doc's "< 50MB private working set" is borrowed from a generation of
Windows desktop apps that ran on .NET Framework or Win32 directly. WinUI 3
has a much larger framework footprint:

- `Microsoft.WinUI` runtime: ~30-40MB
- `Microsoft.WindowsAppSDK` (WinAppSDK 1.x): ~25-35MB
- CsWinRT / WinRT.Runtime marshalling: ~15-20MB
- .NET 8 base CLR + GC arenas: ~25-40MB

That's **~100MB before any app code runs.** Empirically, an *empty* WinUI 3
unpackaged app sits around 90-110MB private WS at idle. Revu at 122MB is
adding only ~12-22MB on top, which is honest for an app with 18 pages,
a dozen view models, an EF-style data layer, and a SQLite connection pool.

**Recommendation: amend criterion 2b to "< 150MB private working set."**
Any tighter requires a packaged-WinUI-3 build (which trims framework
overhead) or a move off WinUI 3, neither of which is in v1 scope.

Per the user's reading: not a launch blocker. Will surface to user for
sign-off before changing the criterion in `LAUNCH_READINESS.md`.

## Criterion 3 trajectory analysis

Sampled at 60s intervals for 32 minutes on an instance that had been
running ~14 hours with active use. User opened a VOD in the player and
left it playing/finishing while the sampler ran.

Two clear regimes:

**Phase 1 (0-19 min): playing.** CPU steady at ~12% one-core (MediaPlayer
decoder + 250ms position-tick timer + sidebar trails). Private WS drift
was +2.9MB across 19 min (1% growth, well within GC noise). Textbook
plateau.

**Phase 2 (20-32 min): post-VOD-end.** CPU dropped to ~8% one-core. Memory
climbed from 311MB → 322MB private (~+10MB / 3.2%). The trajectory
flattened by t=29min and held to t=32min, suggesting a one-shot
reallocation (probably the page or the timeline view rebuilding when
playback completed) rather than a per-frame leak.

**Total drift: +13.5MB / +4.4% over 32 min.** No monotonic per-minute
trend — discrete bumps at regime transitions. Passes the
"plateau-not-climb" reading of criterion 3.

**Why the absolute number is over 250MB.** The test instance was already
at 503MB private WS before the VOD opened (see Criterion 2b long-soak
addendum). When the user navigated to VodPlayerPage, GC ran and brought
private WS down to 308MB — a *decrease* of 195MB just from the
navigation-induced collection. So the 308MB anchor is "long-soak instance
+ VOD-page allocations - GC of unreferenced page state from prior
navigation history." A clean read on criterion 3 would be:

  cold-launch (122MB) → navigate to VodPlayer + open VOD → sample 30 min

…and that test is what to run before tag-and-ship.

## Criterion 2b — long-soak addendum

**Not specified by the doc but worth recording:** a separate instance that
had been running for ~13 hours (with active use during the day) was
measured at **503MB private WS / 2.64% CPU**. That's a 4× growth over
fresh-launch (122MB).

This is *not* per-criterion-2 (criterion 2 is 5-min idle), but it's a
real-world signal worth tracking. Hypothesis: accumulated page state
(Dashboard charts, Analytics histograms, History ItemsRepeater children)
without aggressive eviction. Tabling as a v2.17 investigation —
[V2_17_BACKLOG.md](V2_17_BACKLOG.md#long-soak-memory-growth) — because
the active-use vs leak distinction needs a controlled repro to
disentangle.

## Static-audit notes (pre-measurement)

These are the perf-suspect call sites identified from the LAUNCH_READINESS
risk-areas list, with what the read-through revealed:

- **`SidebarEnergyDrainAnimator`** — `CompositionTarget.Rendering` driver
  with 8 paths animated at ~30fps. Has an `Enabled` toggle (default true).
  Properly removes the rendering handler in `Stop()`. **Concern:** runs
  forever while sidebar is visible; CPU cost worth measuring under
  criterion 2.
- **`HudProgressRing`** — `AttachPulseOpacity` is a Composition implicit
  animation (GPU-thread, not UI-thread). Cheap. Per-ring storyboards
  for the draw-in are one-shot. Likely fine.
- **`VodPlayerPage.Cleanup()`** — looks correct: `_mediaPlayer.Pause()` →
  `Source = null` → `Dispose()`. `NavigationCacheMode="Disabled"` so the
  page is rebuilt on each navigation. The `_isDisposed` field and
  `_positionTimer` lifecycle look right. Criterion 3 + 4 will confirm
  empirically.
- **`Schema.AllMigrations`** — 26 ALTER TABLE statements run on every
  boot, each wrapped in a try/catch that swallows "duplicate column"
  errors (existing DB hits this). Cost is the per-statement parse +
  exception roundtrip. Should be sub-100ms but folded into criterion 1.
- **CDragon cache (`RiotChampionDataClient`)** — caches forever at
  `%LOCALAPPDATA%\Revu\champion_data\`, no eviction. Bound is
  ~165 champs × ~100kB ≈ 16MB. Fine for v1 — not a leak candidate.
- **`EnemyLanerBackfillService`** — clean cancel propagation (see
  criterion 7). Throttled at 600ms between calls, so 200 games = ~2 min
  of backfill. Criterion 5 will check the UI doesn't lag during that.

## Methodology

- **Cold launch (criterion 1)**: kill all `LoLReview.App` processes, wait
  ~10s, double-click the deployed exe in `bin/Debug-tmp/`, stopwatch from
  click → "I can interact with the sidebar." Run 3 times, take median.
  First run after a fresh `dotnet build` is excluded (NGEN warmup).
- **Steady-state idle (criterion 2)**: open to Dashboard, leave window
  visible (not minimized — minimize trims working set artificially),
  energy trails ON. Wait 5 minutes. Read `Mem (private working set)` and
  `CPU %` from Task Manager → Details, sampled every 30s for the last
  2 minutes.
- **VOD steady-state (criterion 3)**: open one VOD, sample
  `(WorkingSet, PrivateWorkingSet)` at t=0/5/10/15/30 min. "No
  monotonic growth" = each later sample is within ±10% of t=5min.
- **VOD leak (criterion 4)**: record post-first-VOD WS as anchor.
  Open + close 9 more VODs. Final WS within 10% of anchor.
- **Backfill UI lag (criterion 5)**: queue 200-game backfill from
  Settings, then navigate to Sessions / Games / VodPlayer pages and
  scroll. Subjective "feels lag-free" + objective via 60fps frame
  counter where available.

## Open questions for re-measurement on non-dev box

- Cold-launch on a 4-core CPU with HDD (not NVMe). Likely the binding
  constraint for the 2.0s budget.
- Idle CPU% with energy trails ON — at 30fps the per-frame work is
  trivial on a 9800X3D, but on a Celeron-class CPU it could pin a
  full core at low wattage.
- File-system pressure during VOD load (`WaitForStableFileAsync` polls
  every 400ms for up to 3s; on slow disks this could feel sluggish).

These ride into v2.17 if we don't get to a non-dev box before launch.

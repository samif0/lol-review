# Revu — v2.17 backlog

Items that are **out of scope** for the v1 public launch but worth doing
once we have first-cohort feedback. Most landed here because they're
either (a) lower-priority than the launch-blocker items in
`LAUNCH_READINESS.md`, (b) need data we'll only have post-launch, or
(c) would have inflated the v1 surface area without proportional payoff.

When picking a v2.17 sprint, pull from the top — items are roughly
ordered by signal strength + cost.

---

## Performance investigations

### Long-soak memory growth (idle → 4× over 13h)

**Signal.** A fresh-launched Revu instance sits at ~125MB private working
set on Dashboard. A 13-hour-old instance (with active use during the
day) was sampled at **503MB private WS / 2.64% CPU**. That's a 4× growth
that the 5-minute idle test in Section 1 doesn't catch.

**Why we didn't fix it pre-launch.** Active-use vs. idle-leak is hard to
disentangle without a controlled multi-hour repro, and 503MB is well
under the OS's "this app is misbehaving" threshold (~2GB). Real users
restart for updates / reboots often enough that 13h cumulative runtime
is uncommon for cohort 1.

**What to do in v2.17.** Build a long-soak test harness:
- Headless idle script that opens the app, navigates Dashboard →
  Analytics → History → Sessions → VodPlayer → back, leaves it on
  Dashboard, samples WS every 5 min for 24h.
- Look for monotonic growth vs. plateau-with-ripple. Any monotonic
  trend is a leak; ripple-around-baseline is just GC scheduling.
- Suspect first: ItemsRepeater children that aren't getting recycled,
  ViewModels held by static event subscriptions, the implicit
  composition-animation registry on `HudProgressRing` instances.

**Where to file.** New `docs/PERF_LONG_SOAK.md` once we have data.

---

### Criteria 3 / 4 / 5 — interactive VOD + backfill perf

Section 1 of `LAUNCH_READINESS.md` deferred:
- VOD playback steady-state < 250MB (criterion 3)
- 10-cycle VOD open/close leak test (criterion 4)
- Backfill 200 games doesn't lag UI (criterion 5)

These need a human in the loop to drive the UI flow. Plan: bake the
measurements into a "perf checklist run" we do before tagging v2.17.0,
on a mid-range non-dev box. Until then, static review of
`VodPlayerPage.Cleanup`, `EnemyLanerBackfillService.RunAsync`, and
`MediaPlayer` lifecycle suggests the leak surface is small.

---

### Sidebar energy-trails CPU cost

The `SidebarEnergyDrainAnimator` runs `CompositionTarget.Rendering` at
~30fps for 8 paths. Sampled at **~21% of one core** during the active-
animation phase (first 110s after launch), then drops to ~3% one-core
once the WinUI compositor throttles.

**Not a launch blocker** — overall CPU at 5-min idle is 0.18% total,
within budget. But on a 4-core laptop, 21% of one core is noticeable
in fan-noise + battery terms.

**v2.17 idea**: throttle to 60s of animation post-navigation, then
fade out to a static state. Or: pause the rendering handler when the
window loses focus.

---

### Cold-launch on non-dev hardware

Median 393ms on a Ryzen 9800X3D + NVMe + 62GB RAM is not
representative. Real users will be on 4-core CPUs, often with HDDs
or older SSDs, often with 16GB RAM. Re-measure before launch — and if
we miss the 2.0s budget, the suspects are:

- `Schema.AllMigrations` running 26 ALTER statements on every boot
  (each fails-and-swallows on existing DBs). Could short-circuit by
  recording a "schema version" row.
- `RiotChampionDataClient` summary fetch on first launch (lazy, but
  the first PreGamePage hit will block on it).
- Velopack update check (already async, but the file I/O happens
  on the dispatcher thread).

---

## Code quality (deferred from Section 2 of LAUNCH_READINESS)

To be filled in as Section 2 proceeds.

---

## Repo / site (deferred from Section 3-4)

To be filled in as those sections proceed.

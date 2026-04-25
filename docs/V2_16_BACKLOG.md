# Revu v2.16 Backlog

Forward-looking backlog after v2.15.10 ships. Each item stands alone — paths, why it matters, scope hints — so a fresh session can act without re-reading prior conversations.

---

## Bugs

### VOD viewer scroll shake/stutter near bottom
- One-line: scrolling near the end of any page caused a visible shake / stutter.
- **Why:** Degrades feel app-wide; user reported on every long-scroll page.
- **Scope:** Mitigated v2.15.10 by setting `IsScrollInertiaEnabled="False"` on all 14 page-level ScrollViewers (Dashboard, Analytics, History, ManualEntry, ObjectiveGames, ObjectiveNotes, Objectives, Onboarding, PostGame, PreGame, Review, Rules, SessionLogger, Settings, TiltCheck). Verify the symptom is fully gone after release. If it persists, the cause is something else — candidates: page-enter animation re-firing, `GameRowCard` hover effects firing as cards scroll past the cursor, or composition-clip math in `ShellPage.xaml.cs` (`UpdateContentViewportClip`).
- **Status:** v2.16 landed (pending build verification) (mitigation shipped, verification pending — defer to user testing)

### Rule-break clear may re-flag on re-evaluation
- One-line: clearing a false-positive rule break only updates `session_log.rule_broken`.
- **Why:** If the rules engine re-evaluates the same game's condition (on rule edit, app restart, etc.), the flag could come back — clears feel non-sticky.
- **Scope:** Repro by clearing a flag, then forcing rule re-eval. If it re-flags, add a per-game `user_cleared_rule_break` stamp the engine respects. Files: `src/Revu.Core/Data/Repositories/SessionLogRepository.cs`, `src/Revu.Core/Data/Schema.cs` (migration), wherever rules eval lives in `Revu.Core`.
- **Status:** v2.16 landed (pending build verification) (needs repro)

### Priority objective unlabeled on Dashboard active-objectives list
- One-line: the priority objective on Dashboard's "ACTIVE OBJECTIVES" card looks visually identical to non-priority rows.
- **Why:** Users can't tell which objective is the active priority at a glance — current visual cue is too subtle (probably gold ring vs purple).
- **Scope:** Add an explicit "PRIORITY" badge or label next to the title. Files: `src/Revu.App/Views/DashboardPage.xaml` (active-objectives ItemsRepeater template), `src/Revu.App/ViewModels/DashboardViewModel.cs` (`DashboardObjectiveItem` — likely needs `IsPriority` flag).
- **Status:** v2.16 landed (pending build verification)

### Confusing terminology: "primary" vs "priority"
- One-line: drop "primary" anywhere it appears for objectives — keep only "priority."
- **Why:** Two words for the same concept; users get confused.
- **Scope:** Grep `Primary`/`primary` across `src/Revu.App` + `src/Revu.Core`, replace where it refers to objectives. Watch for: `IsPrimary` properties, settings labels, prompt text, onboarding copy. Don't blanket-replace — only objective contexts.
- **Status:** v2.16 landed (pending build verification)

### 0% objective shows a "completed bar" arc on the progress ring
- One-line: at 0% the ring renders as if it has progress.
- **Why:** Visual regression. We thought this was fixed in v2.15.10 (`HudProgressRing` ctor sets dash array before DP callbacks fire) but the screenshot shows it persisting.
- **Scope:** Investigate `src/Revu.App/Controls/HudProgressRing.xaml.cs` — the dash-array-in-ctor change should have fixed it. Possible: another path sets `StrokeDashOffset` before the ctor's dash array is applied, or the issue is at 0% specifically (offset == circumference, but rounding/clamp could leave a tiny visible arc). Repro: fresh objective at score 0.
- **Status:** v2.16 landed (pending build verification) (regression — was supposedly fixed)

### Hard reset issues
- One-line: `Settings → Danger Zone → Reset all data` doesn't fully work.
- **Why:** User said "fix hard reset issues." Specifics unknown — could be: backup file collision (timestamped name conflict), file delete retry timeout, config not wiped, or post-exit relaunch races a write.
- **Scope:** Repro the user's specific failure mode first. Files: `src/Revu.Core/Services/BackupService.cs:177` (`ResetAllDataAsync`), `src/Revu.App/ViewModels/SettingsViewModel.cs:813` (`ResetAllDataAsync` command). Look at recent v2.15.4 fixes for the WAL checkpoint + ClearAllPools dance — verify that path still holds.
- **Status:** v2.16 landed (pending build verification) (needs repro + diagnosis)

### Auto-mark objective as practiced when a clip/note is attached
- One-line: attaching a bookmark or clip to an objective on PostGame should flip that assessment's `Practiced = true`.
- **Why:** Currently the user has to remember to toggle the practiced checkbox separately. If they bothered to clip something for the objective, they practiced it — the toggle is redundant friction.
- **Scope:** Hook into the bookmark/clip tag-write path. When `_vodRepo.SetBookmarkTagAsync` or `AddBookmarkAsync` lands an `objectiveId` for a given gameId, call `_objectivesRepo.RecordGameAsync(gameId, objectiveId, practiced: true, executionNote: ...)` if not already practiced. Files: `src/Revu.App/ViewModels/VodPlayerViewModel.cs` (`AddBookmarkAsync`, `SetBookmarkTagAsync`, clip-extract path), `src/Revu.Core/Data/Repositories/ObjectivesRepository.cs`. Edge: if the user later untags the bookmark, do NOT auto-revert (user-set state wins).
- **Status:** v2.16 landed (pending build verification)

### Enemy laner missing on older games
- One-line: matchup pills show champion alone for legacy entries.
- **Why:** v2.15.8 added the "Champ vs Enemy" pill, but games saved before then have empty `enemy_laner`.
- **Scope:** `EnemyLanerBackfillService` drains 50/launch via Riot Match-V5. After several launches, verify recent picks resolve. If drain stalls, inspect throttle config + proxy 429s. Files: `src/Revu.Core/Services/EnemyLanerBackfillService.cs`, `src/Revu.App/ViewModels/ShellViewModel.cs` (auto-trigger).
- **Status:** v2.16 landed (pending build verification) (drain in progress for users)

---

## Features

### Settings page rebuild with categorization
- One-line: regroup Settings into navigable categories instead of one long scroll.
- **Why:** Current `SettingsPage.xaml` is a single StackPanel of `CornerBracketedCard`s; users scroll to find anything.
- **Scope:** Categories — Recordings/VOD, Backups, Riot Account, Appearance, Danger Zone. Implementation choice: left-rail nav within the page, or accordion expanders. Files: `src/Revu.App/Views/SettingsPage.xaml` (+ a new nav control). Out of scope: underlying setting logic — only re-organize the surface.
- **Status:** v2.16 landed (pending build verification)

### Make objective-leveling rules visible to the user
- One-line: surface how an objective levels up (Exploring → Drilling → Ingraining → Ready @ 50pts).
- **Why:** Users don't know the threshold rules; the level changes silently and they can't predict when "Ready" unlocks.
- **Scope:** Show progression visually inside the Objectives card (current level + next threshold + delta). Optionally: one-time tooltip the first time an objective levels. Math is already in `IObjectivesRepository.GetLevelInfo` — just expose it visually. Files: `src/Revu.App/Views/ObjectivesPage.xaml`, `src/Revu.Core/Data/Repositories/IObjectivesRepository.cs`.
- **Status:** v2.16 landed (pending build verification)

### "Start Block" pre-queue ritual
- One-line: short flow before queueing that primes the user — session goal + priority objective + 30s mental check-in.
- **Why:** PreGamePage already exists for champ-select, but by then the user is already locked in. Start Block fires earlier so the user enters queue with a focused intent.
- **Scope:** New page or dialog. Open question: trigger — manual button, or auto-detect when LCU is on the home screen? Files: new `src/Revu.App/Views/StartBlockPage.xaml` + nav route + a trigger in `GameMonitorService` or a manual button on Dashboard.
- **Status:** v2.16 landed (pending build verification) (trigger TBD)

### Add app uninstaller / "Add or Remove Programs" entry
- One-line: Revu currently has no Windows uninstall entry.
- **Why:** Users can't cleanly remove the app — Velopack installs but doesn't register an uninstall command.
- **Scope:** Investigate Velopack / Squirrel `--uninstall` hooks. Likely a flag in the Velopack pack config or a `Setup.exe /uninstall` shortcut on install. Files: `src/Revu.App/Revu.App.csproj` (pack settings), `src/Revu.App/Program.cs` (Velopack ctor flags), CI release workflow if needed. Reference: Velopack docs on Squirrel command-line.
- **Status:** v2.16 landed (pending build verification)

### Role-aware matchup pairings
- One-line: matchup pills + review headers should include the relevant adjacent role's matchup, scoped to the user's `PrimaryRole` config.
- **Why:** "Kai'Sa vs Tristana" tells an ADC half the story — the bot lane is a 2v2. A mid laner cares about the jungler-mid pairing more than just the lane opponent. Surfacing both makes it easier to find a specific game later ("the Kai'Sa game where I had a Nautilus into Tristana/Renata") and primes review of the right lane dynamics.
- **Pairing rules:**
  - **ADC** → "Kai'Sa+Nautilus vs Tristana+Renata" (own ADC + own support vs enemy ADC + enemy support).
  - **Support** → mirror of ADC (own support + own ADC vs enemy support + enemy ADC).
  - **Mid** → "Ahri+Lee vs Syndra+Graves" (mid + own jungler vs enemy mid + enemy jungler).
  - **Jungle** → "Lee+Ahri vs Graves+Syndra" (own jungler + mid vs enemy jungler + mid).
  - **Top** → fall back to current `Champion vs EnemyLaner` (no obvious adjacent pairing — top is more isolated).
  - When `PrimaryRole` is unset, fall back to current behavior.
- **Scope:**
  - Pull all 10 participants per game during the existing Match-V5 backfill, not just the opposite-laner. Add columns / a JSON blob to `games` storing role → champion mapping for both teams. Files: `src/Revu.Core/Services/EnemyLanerBackfillService.cs` (`ExtractEnemyLaner` already finds the user's row — extend to capture `OwnSupport`, `OwnJungle`, `EnemyJungle`, etc.), `src/Revu.Core/Data/Schema.cs` (migration), `src/Revu.Core/Models/GameStats.cs`.
  - VM derives the display string from `PrimaryRole` + the participant map. Files: `src/Revu.App/ViewModels/DashboardViewModel.cs:GameDisplayItem.ChampionDisplay`, `SessionLoggerViewModel.SessionGameEntry.ChampionDisplay`, `ObjectivesViewModel.SpottedProblemItem.ChampionDisplay`, `ReviewViewModel.MatchupHeading`.
  - Live game-end ingest also needs to populate the new fields (`MatchHistoryReconciliationService` + LCU `GetMatchDetailsAsync`). LCU summary payload usually has all 10 participants with positions — verify before adding a Match-V5 round-trip.
  - Settings: nothing new — `PrimaryRole` already lives on `IConfigService`.
  - Edge cases: ARAM and other non-positional queues have no `teamPosition` — fall back to lane-only or hide the pairing.
- **Status:** v2.16 landed (pending build verification)

### VOD viewer timeline redesign
- One-line: timeline currently shows unlabeled colored lines — replace with labeled markers.
- **Why:** User said: "currently its a bunch of lines that have no meaning to the user. maybe some text on the timeline space." Markers carry semantic info (Kill, Tower, Death, Bookmark, Derived event) but only color encodes that, which doesn't scale or accommodate colorblindness.
- **Scope:** Inline text labels per marker (e.g., "Kill", "Twr", "BM"), with collision-avoidance for dense regions, plus on-hover tooltips for the full event. Files: `src/Revu.App/Controls/TimelineControl.xaml` + `.xaml.cs` (custom Canvas-rendered control). Existing data: `TimelineEvent` model.
- **Status:** v2.16 landed (pending build verification)

---

## Polish

### Replace VOD viewer auto-fullscreen with a "minimum usable" window resize
- One-line: stop force-fullscreening the post-game VOD viewer; instead grow the window to at least the size where the bookmark/clip column fits without clipping.
- **Why:** v2.15.9 auto-fullscreened on every VOD entry to fix the bookmark column getting clipped at small window sizes. One user found the takeover annoying. Compromise: keep the user's prior window state, but if it's smaller than what the VOD layout needs, bump it up to a "viewable VOD + visible side panels" minimum. If the user's window was already bigger than the minimum, leave it alone. Goal: bookmark/clip tiles look identical to fullscreen layout (same size, same density), so a too-narrow window naturally invites the user to widen it themselves.
- **Scope:**
  - Drop the `if (!_isFullscreen) ToggleFullscreen()` in `VodPlayerPage.xaml.cs:OnNavigatedTo` (added v2.15.9).
  - On entry: read `AppWindow.Size`. If width < some threshold (say 1400px) or height < 800px, call `AppWindow.Resize(new SizeInt32(max(currentW, 1400), max(currentH, 800)))`. Don't shrink the window if it was already bigger.
  - On `OnNavigatedFrom`: drop the `Default` presenter restore (no longer fullscreen-toggling).
  - Layout: verify `<Grid ColumnDefinitions="3*,*">` in `VodPlayerPage.xaml` — at the threshold size the right column should hold the bookmark cards comfortably. May need to set `MinWidth` on the right column or compute thresholds from there.
  - Files: `src/Revu.App/Views/VodPlayerPage.xaml.cs` (entry hook + size logic), `src/Revu.App/Views/VodPlayerPage.xaml` (column min-widths if needed). Reference: `Microsoft.UI.Windowing.AppWindow.Resize`, `Windows.Graphics.SizeInt32`.
- **Status:** v2.16 landed (pending build verification)

### Live progress on Backfill matchups Settings card
- One-line: card shows only final-status text; hide indicator during a 200-game run.
- **Why:** Long runs look hung. User has no signal that anything is happening between click and finish.
- **Scope:** Add `scanned X of Y` live updates. Probably: an `IProgress<int>` parameter on `EnemyLanerBackfillService.RunAsync` plus a bound TextBlock in `SettingsPage.xaml`. Files: `src/Revu.Core/Services/EnemyLanerBackfillService.cs`, `src/Revu.App/ViewModels/SettingsViewModel.cs`, `src/Revu.App/Views/SettingsPage.xaml`.
- **Status:** v2.16 landed (pending build verification)

### Post-game review hero matchup heading affordance
- One-line: when enemy never resolves (manual entry, off-region), surface a "+ add enemy" button instead of silently falling back.
- **Why:** Currently `MatchupHeading` shows "Champion" alone when `HasEnemyLaner == false`. Some games will never resolve via the API; user has no path to fix.
- **Scope:** Inline editable surface or a small button that opens a quick-pick dialog. Files: `src/Revu.App/ViewModels/ReviewViewModel.cs`, `src/Revu.App/Views/PostGamePage.xaml`.
- **Status:** v2.16 landed (pending build verification)

---

## Investigations

### Velopack delta-package corruption + update fallback fragility
- One-line: deltas frequently fail with "Data corruption detected"; on-machine fallback to full package masks the problem, but users with slow networks / AV / low disk get stuck.
- **Why:** Log evidence from a working machine shows three deltas in a row failing during 2.15 development (2.12 → 2.13, 2.15.0 → 2.15.4, 2.15.4 → 2.15.8) with `Patch error: Delta error: Data corruption detected` at the zsdiff stage. Each time, Velopack downloaded the full ~150MB+ package and recovered. A specific user reported 2.15.9 → 2.15.10 didn't update for them despite the build being good — likely the same delta corruption, but their full-package fallback failed for an environmental reason (slow network mid-download, AV mid-scan, low disk for 2x extract, app closed mid-download).
- **Two threads to investigate:**
  1. **Why are deltas corrupting in the first place?** Likely culprits — non-deterministic builds (timestamps in embedded resources, source-link variability, .deps.json ordering), GitHub release re-uploads producing a hash mismatch against a previously-cached base, or a Velopack zsdiff bug at large file sizes (`ffmpeg.exe.zsdiff` triggered "Large File detected. Overriding windowLog to 28" on the 2.13.1 → 2.15.0 path). Test by hand-diffing two consecutive nupkgs and verifying determinism. Reference: https://reproducible-builds.org/docs/source-date-epoch/.
  2. **User-facing diagnostics for failed updates.** Currently the user has no signal that an update failed silently. Two adds: (a) on app start, log + telemetry the last `releases.win.json` localVersion vs the running build — if they diverge for >24h, surface a "Update is stuck on vX.Y.Z, here's how to fix" banner; (b) bundle a "Diagnose update" button in Settings → Updates that surfaces the last 50 lines of `Velopack.log` so the user can paste it without us walking them through file paths.
- **Scope:** Files: `src/Revu.App/Program.cs` (Velopack ctor), `src/Revu.App/Services/UpdateService.cs`, `src/Revu.App/Views/SettingsPage.xaml` (diagnose button + UI). For determinism: `src/Revu.App/Revu.App.csproj` Velopack pack settings + `.github/workflows/release.yml`. Reference: Velopack docs on log file location (`%LocalAppData%\<AppId>\Velopack.log`).
- **Status:** v2.16 landed (pending build verification)

### DB safety on user delete of revu.db / -wal / -shm
- One-line: if a user manually deletes those files, SQLite silently recreates empty + Initializer reseeds. All games / reviews / objectives / bookmarks gone.
- **Why:** No detection, no banner, no auto-recovery. Backups folder rotation exists but the user has to know to look there.
- **Scope:** Two follow-ups, decide which to ship:
  - **First-run-vs-data-loss detection** — write a sentinel file after first init. If the sentinel is present but the DB just got recreated empty, show a banner pointing to `Settings → Restore from backup`.
  - **Auto-backup on every app launch** — cheap WAL-checkpointed copy into rotation. Belt-and-suspenders so even pre-delete data is recoverable from the backups folder.
- **Status:** v2.16 landed (pending build verification) (scope decision pending)

### Pre-game prompt answers in Review
- One-line: pre-game answers ARE saved to `prompt_answers` keyed by gameId, but `ReviewViewModel.HydratePromptsAsync` skips `Phase == PreGame` intentionally.
- **Why:** User asked to surface them. Original design treated pre-game as champ-select-only context.
- **Scope:** Decide between two surfaces:
  - **Read-only summary** — at top of each objective card, "Pre-game intent: ..." line. Lighter touch, matches original intent.
  - **Editable section** — full prompt fields rendered like ingame/postgame, lets user revise.
  - Files: `src/Revu.App/ViewModels/ReviewViewModel.cs:213` (the skip filter), `src/Revu.App/Views/PostGamePage.xaml` (per-objective card).
- **Status:** v2.16 landed (pending build verification) (decision pending)

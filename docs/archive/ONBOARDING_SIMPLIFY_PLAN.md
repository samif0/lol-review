# Onboarding + Dashboard simplify — handoff plan

Goal: address real user feedback that the app is "too complicated, not
simple enough." Scope is deliberately narrow. Every other part of the
app stays as-is.

Status: **not yet executed.** Starts from `main` at or after commit
`5b0a7ad` (the v2.13.1 analytics-filter ship).

Time estimate: **~1.5 hours** end-to-end, in one session.

---

## Background — how we got here

The feedback came via a user who told the owner the app was "too
complicated." No specific moment was called out. We talked through the
likely culprits and ruled out the obvious ones:

- **Rules vs Objectives** are NOT the same thing — owner confirmed they
  have distinct mental models (commitments vs. practice skills). Do NOT
  merge these pages.
- **Post-game review boxes** are mostly useful. Separate boxes for
  Mental / Matchup Notes / Spotted Problems help the user think. Don't
  collapse.
- **Concept Tags** — appeared cuttable, but a DB check on the owner's
  live data showed 35% of games tagged with meaningful patterns
  ("Team diff", "Tilted", "Overextended"). **Keep.**
- **Analytics page** — "hide until 10 games" is too aggressive. Power
  users will want it from day one.
- **Sidebar nav (9 items)** — left alone for now. Removing items
  breaks mental models for existing users.

What's left as real, actionable friction for a new user:

1. **Onboarding has too many steps.** Currently 5-slide tour + role +
   Ascent + first-objective + optional auth = ~8 screens before they
   can do anything.
2. **Empty-state Dashboard feels broken.** Four stat cards showing
   "0W 0L" and "0 games" reads as "this app is blank" — not a
   welcoming first impression.

Everything else stays.

---

## Scope

### In scope for this ship

- Cut onboarding to **3 screens**: Welcome (w/ optional auth link) →
  Role pick → Done → lands on Dashboard.
- **Move content out of the tour into contextual Dashboard prompts:**
  - Ascent folder → Dashboard card, shown until connected or dismissed.
  - First objective → Dashboard card, appears after the user has
    reviewed 3 ranked games.
- **Dashboard empty-state overhaul** — for users with 0-2 reviewed
  games, replace the blank stat cards + empty queue with a single
  "Next step" card whose content depends on state:
  - 0 games: "Play a ranked game. Revu captures it automatically."
  - 1-2 games unreviewed: "Your last game is ready to review."
    (button → session/review flow)
  - 3+ games reviewed, no active objective: "Set your first
    objective — here are ideas based on your reviews." (button →
    objectives page pre-seeded with Spotted-Problem-derived
    suggestions)
- Full stat strip + Active Objectives panel returns once the user has
  5+ reviewed games. Below that threshold, only the Next Step card is
  visible on the Dashboard.

### Explicitly NOT in scope

- Rules / Objectives merge — do NOT do this.
- Post-game review slim-down — leave the 6-ish field layout alone.
- Concept Tags — keep.
- Sidebar pruning — leave all 9 nav entries.
- Analytics page changes — the filter bar just shipped, leave it.
- HUD aesthetic / density — leave the mono eyebrows + corner brackets.
- Tooltip-based inline tour (mentioned in earlier drafts) — defer.

### Deliberately leaving undecided

Whether the "Next step" card should show a dismiss button. Default to
**no dismiss** for the lowest-data states (0-2 games) because those
users need the prompt. Add dismiss once the user has 3+ games and is
being nudged toward objectives.

---

## Files to touch

From the v2.13.1 tree on `main`:

```
src/Revu.App/
  ViewModels/
    OnboardingViewModel.cs    — remove tour states, remove objective-
                                creation command, simplify state
                                machine
    DashboardViewModel.cs     — add empty-state logic + NextStepCard
                                observable + Ascent-prompt observable
                                + first-objective-prompt observable
  Views/
    OnboardingPage.xaml       — remove tour cards (tourWhat, tourLoop,
                                tourAscent, tourHabits, tourObjective)
    DashboardPage.xaml        — add NextStepCard section at top,
                                hide stat strip + today's games +
                                active objectives below threshold
```

Core stays untouched.

---

## Ordered step list (for the executing session)

1. **Survey current OnboardingViewModel state machine** — confirm the
   states exist as listed (`welcome`, `emailEntry`, `codeSent`,
   `account`, `role`, `tourWhat`, `tourLoop`, `tourAscent`,
   `tourHabits`, `tourObjective`, `done`). Reference:
   [src/Revu.App/ViewModels/OnboardingViewModel.cs:30](../src/Revu.App/ViewModels/OnboardingViewModel.cs).

2. **Remove tour states** from the VM + XAML. After role pick, go
   straight to `done` and fire `Completed`. Keep the
   `StartUsingRevuCommand` and login-path commands — only the tour
   cards are being cut.

3. **Kill `CreateFirstObjectiveAsync` + `SkipTourObjectiveAsync`
   commands** in OnboardingViewModel — they belong on the Dashboard
   now. Their SQL side-effect (writing one objective row) moves to a
   new `CreateFirstObjectiveCommand` on DashboardViewModel.

4. **Add DashboardViewModel state properties** for empty-state:
   ```
   public enum DashboardStage {
       NoGames,            // 0 games
       HasUnreviewed,      // 1-2 games, any unreviewed
       NeedsObjective,     // 3+ reviewed, 0 active objectives
       Normal              // 5+ reviewed, has objectives
   }
   ```
   Compute this in `LoadAsync` from existing queries. Add observable
   `Stage` and drive card visibility from it.

5. **Add `NextStepTitle` / `NextStepBody` / `NextStepCta` observables**
   + `NextStepCommand` that branches on `Stage`:
   - NoGames → no button, just the message
   - HasUnreviewed → "REVIEW NOW" → nav to session
   - NeedsObjective → "SET OBJECTIVE" → nav to objectives with a
     pre-seeded suggestion
   - Normal → Next Step card hidden entirely

6. **Add Ascent reminder card** (separate from Next Step card):
   visible when `config.AscentFolder` is empty AND the user hasn't
   dismissed it. Store dismissed flag as new bool in AppConfig:
   `AscentReminderDismissed`. Button opens the folder-picker (same
   helper as SettingsViewModel.PickFolderAsync); on pick, save to
   config and hide the card.

7. **Gate the existing Dashboard sections** (stat strip, Today's
   Games, Active Objectives) behind `Stage == Normal`. Below that,
   render only Next Step + Ascent reminder.

8. **Onboarding XAML cleanup** — delete the 5 tour CornerBracketedCard
   blocks. State transitions stop at `role` (+ `done` fires
   immediately after role pick).

9. **DashboardPage XAML** — add the two new cards at the top of the
   content ScrollView, wire visibility to the new observables.

10. **Test matrix on the owner's machine** (he can do this from his
    laptop):
    - Reset `onboarding_skipped=false` + clear session +
      `ascent_folder=''` in `%LOCALAPPDATA%\LoLReviewData\config.json`
    - Launch → onboarding = 3 screens
    - Land on Dashboard with only a "Play a ranked game" Next Step
      card + an "Add Ascent folder" reminder
    - Play/import 1 game → Next Step changes to "Review now"
    - Review 3 games → Next Step changes to "Set your first objective"
    - Set an objective → full Dashboard materializes
    - Connect Ascent folder → reminder disappears

11. **Build + smoke test** before committing. Standard:
    ```
    /c/Program\ Files/Microsoft\ Visual\ Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe \
      src/Revu.App/Revu.App.csproj -p:Configuration=Debug -p:Platform=x64 -v:q -m
    dotnet test src/Revu.Core.Tests/Revu.Core.Tests.csproj -c Debug -p:Platform=x64 \
      --logger "console;verbosity=minimal" --nologo
    ```

12. **Ship as v2.14.0.** Bump `<Version>2.13.1</Version>` →
    `<Version>2.14.0</Version>` in `src/Revu.App/Revu.App.csproj`.
    Commit message pattern (matches prior style):
    ```
    onboarding: cut tour + move prompts to dashboard empty-state, v2.14.0
    ```
    Then `git tag v2.14.0 && git push origin v2.14.0` — the workflow
    at [.github/workflows/release.yml](../.github/workflows/release.yml)
    takes over from there.

---

## Gotchas the next session will hit

1. **OnboardingSkipped config flag semantics.** Currently
   `OnboardingComplete` returns `true` when `OnboardingSkipped` OR
   (`RiotProxyEnabled` AND `PrimaryRole`). After this change, the
   skip-path user still completes role pick, so the onboarding gate
   works the same. No migration needed.

2. **First-objective suggestion seeding.** The new Dashboard prompt
   says "here are ideas based on your reviews." The existing
   `IAnalysisService.GenerateSuggestions(profile)` returns up to 3
   `ObjectiveSuggestion`s using 7 deterministic rules. Call that from
   DashboardViewModel when the user hits "SET OBJECTIVE" and pass the
   top suggestion into the navigation param so ObjectivesPage can
   pre-fill the create form. Don't reinvent the suggestion logic.

3. **`AscentReminderDismissed` config field.** Adding a new bool to
   AppConfig requires:
   - Field in `src/Revu.Core/Models/AppConfig.cs`
   - Serialization key (JsonPropertyName) matching the existing
     snake_case convention (`ascent_reminder_dismissed`)
   - Getter on `IConfigService`
   - Default value `false` — no migration needed because absent-key
     deserializes to default.

4. **Dashboard stage threshold = 5 games.** Chosen because:
   - Below 5, stat averages are too noisy (one loss swings win rate).
   - 5 is enough for a "did you play today" adherence streak to feel
     earned.
   - Owner can tune this constant in one place
     (`DashboardViewModel.cs`) if it feels wrong.

5. **Don't delete the `tour*` XAML resources file-globally** — just
   the blocks inside `OnboardingPage.xaml`. There may be image assets
   or styles in other files that reference them; grep before deleting
   anything shared.

6. **Tests.** None of the current 35 tests cover onboarding state
   transitions. Don't add tests for this unless the owner asks. The
   manual test matrix in step 10 is the verification.

7. **First-launch telemetry.** We don't have it. Can't measure whether
   the new flow improves completion rate. Ship on gut + feedback.

8. **Velopack delta.** The onboarding XAML change + DashboardPage
   rewrite are user-facing; the delta nupkg will be modest (~1-2 MB).
   Normal full-install for new users, auto-delta for existing
   installs.

9. **Existing installs mid-onboarding.** A user who happens to be
   staring at the tour when they auto-update to v2.14.0 will see the
   next screen they navigate to. Not a real concern — the VM state
   resets on app relaunch, which Velopack prompts for.

10. **Ascent folder card visibility logic.** Show when:
    `config.AscentFolder == ""` AND `config.AscentReminderDismissed == false`.
    NOT when the folder is set but empty (valid: user pointed at a
    folder with no VODs yet). The current IsAscentEnabled getter on
    ConfigService returns null when unset, non-null when set — use
    that, not raw string comparison.

---

## Rollback

- **Bad deploy**: revert the commit, re-ship as v2.14.1.
- **User hates it**: keep v2.14.0 as-is; don't revert. Instead, add a
  "Show full Dashboard" toggle in Settings that forces `Stage ==
  Normal`. That's a v2.14.2 fix, not a revert.
- **Totally broken onboarding**: users can bypass by manually setting
  `onboarding_skipped=true` in `%LOCALAPPDATA%\LoLReviewData\config.json`
  while app is closed, then restarting. Document this escape hatch in
  the release notes if we ship with uncertainty.

---

## Out-of-scope follow-ups

The conversation that produced this doc surfaced these for later:

- **Inline contextual tooltips** the first time a user visits Session /
  Objectives / History. Replaces the dead tour slides entirely with
  teaching that happens in-context.
- **Post-game review slim-down** — reduce the 6-section form to 3
  required + "add more detail" expander. Deferred until real usage
  signals which fields go unfilled.
- **Sidebar auto-collapse** — show all 9 nav entries, but collapse
  Analytics/Coach/VOD Player behind a "More" expander for new users.
  Low-risk. Defer.
- **Tour-as-help** — park the 5 tour slides under Settings → "How
  Revu works" so the information isn't lost, just moved out of the
  critical path. Trivial addition.
- **Backup pruning for `coach-pre-migration-*.db`** — currently staged
  uncommitted on a working branch. Small 10-line BackupService.cs
  change. Ship alongside v2.14.0 or as its own v2.13.2. Owner's
  choice.

---

## Context on the codebase

The handoff session should start by reading:

- This doc.
- `CLAUDE.md` (owner's preferences, critical rules about never
  overwriting the DB, always backing up, treating crashes as severity-1)
- `docs/CODEBASE_ONBOARDING.md` for general architecture.
- `docs/REVU_LOL_SITE_PLAN.md` is unrelated (landing site, already
  shipped) but shows the handoff-doc tone that has worked before.

Relevant code entry points:

- [src/Revu.App/ViewModels/OnboardingViewModel.cs](../src/Revu.App/ViewModels/OnboardingViewModel.cs)
  — state machine comments at top of class are accurate as of v2.13.1.
- [src/Revu.App/ViewModels/DashboardViewModel.cs](../src/Revu.App/ViewModels/DashboardViewModel.cs)
  — LoadAsync shows every query feeding the current dashboard.
- [src/Revu.App/Views/OnboardingPage.xaml](../src/Revu.App/Views/OnboardingPage.xaml)
  — look for `IsState(ViewModel.State, 'xxx')` binding patterns on
  CornerBracketedCard Visibility.
- [src/Revu.App/Views/DashboardPage.xaml](../src/Revu.App/Views/DashboardPage.xaml)
  — current stat strip + today's games + active objectives layout
  lives in the main ScrollView StackPanel.
- [src/Revu.Core/Services/AnalysisService.cs](../src/Revu.Core/Services/AnalysisService.cs)
  — `GenerateSuggestions` is the function to call for first-objective
  seed.
- [src/Revu.Core/Models/AppConfig.cs](../src/Revu.Core/Models/AppConfig.cs)
  — where the new `AscentReminderDismissed` bool lives.

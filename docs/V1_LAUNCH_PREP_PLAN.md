# Revu v1 Launch-Prep Plan (review before any edits)

Status: **PLAN ONLY — no code changed yet.** Scope agreed with owner:

- ✅ **#5** Atomic `config.json` write (B4) — `Revu.Core`
- ✅ **#4** Login stuck-state fix (B3) — `Revu.App`
- ✅ **#6** **Remove** coaching code for v1 (preserve on a branch), not just flag it off
- ❌ #7 SmartScreen note — **skipped** (owner's call)
- Auth/login — **unchanged**, kept as-is (free Resend tier is fine for launch)
- #2 (web lazy-load) / #3 (lower AGGREGATE_RPS) — **not in this plan**; #2 mostly already
  implemented (retry/backoff/cache/sequential), #3 mooted once the Riot production key lands.

Build constraint that shapes everything below: per project memory, the **WinUI `Revu.App`
project needs Visual Studio MSBuild** (PRI generation); `dotnet build` is unreliable for it.
`Revu.Core` + `Revu.Core.Tests` **do** build with `dotnet`. So Claude can compile-verify Core
changes locally; **App changes must be built by you in VS** and any errors reported back.

Recommended execution order: **#5 → #6 → #4**, each as its own commit on a branch off `main`.
Rationale: #5 is Core-only and independently verifiable; #6 is the largest surgery and should
land before the small #4 tweak so #4 isn't redone; #4 is a quick App-only follow-up.

---

## Item #5 — Atomic config.json write (B4)

**Why:** [ConfigService.SaveAsync](../src/Revu.Core/Services/ConfigService.cs) writes with
`File.WriteAllTextAsync(_configFile, json)` (line ~116), which truncates-then-writes in place.
A crash/power-loss/full-disk mid-write leaves a truncated or empty `config.json`; next launch
the parse fails and the loader **silently resets to defaults** (lines ~184-188), losing
keybinds, Ascent folder, role, etc. (Auth tokens survive — they're in the separate DPAPI
`protected_secrets.bin`.) This violates the project's hard "never lose user data" rule, which
is currently honored for the DB but not for config.json.

**Files touched (1):**
- `src/Revu.Core/Services/ConfigService.cs`

**Change:** replace the in-place write in `SaveAsync` with temp-file + atomic replace:
1. Serialize to `json` (unchanged).
2. Write to a sibling temp file, e.g. `_configFile + ".tmp"` (same directory, so the
   rename is atomic on the same volume).
3. `File.Move(tmp, _configFile, overwrite: true)` — atomic replace on NTFS.
4. On any exception, delete the temp file and rethrow (don't leave `.tmp` litter).

Apply the **same pattern** to the in-place rewrite inside `LoadFromDiskAsync` /
`LoadFromDiskSync` (the secrets-migration sanitize step also calls `File.WriteAllText*`
at lines ~178 and ~209).

**Bonus hardening (recommended, low risk):** when load fails to parse, **back up the
corrupt file** before returning defaults — copy `config.json` →
`config.json.corrupt-<timestamp>` in the catch block (lines ~184-188 and ~215-219) so a
mangled config is recoverable instead of silently discarded. Use a timestamp passed in or
`DateTime` — note this is Core (not a workflow script), so `DateTime.Now` is fine here.

**Second writer to reconcile:** [CoachFeatureFlag.SetEnabled](../src/Revu.App/Services/CoachFeatureFlag.cs)
also writes `config.json` directly with its own serializer (line ~62), bypassing
ConfigService entirely — a second, non-atomic writer that can race the first. **#6 removes
this file**, which eliminates the problem. (If #6 were ever deferred, this would need its own
atomic write. With #6 in scope, no action needed.)

**Verify (Claude can do locally):**
- `dotnet build src/Revu.Core/Revu.Core.csproj -c Release` compiles.
- `dotnet test src/Revu.Core.Tests/Revu.Core.Tests.csproj` — existing config tests
  (`ConfigServiceProtectedSecretsTests`) still pass.
- Optional new test: write config, truncate the file to simulate a partial write, confirm
  load falls back AND a `.corrupt-*` backup was produced.

**Risk:** Low. Self-contained, Core-only, build-verifiable here.

---

## Item #6 — Remove coaching code for v1 (preserve on a branch)

**Decision:** full removal from `main`, not feature-flag. Preserve the current state on a
branch first so it's recoverable.

### Step 0 — Preservation (do FIRST)
```
git branch coach-preservation        # snapshot current main with coach intact
git push -u origin coach-preservation # optional but recommended (offsite copy)
```
Then do the removal on a separate working branch (e.g. `strip-coach`) off `main`.
Net effect: `main` loses coach; `coach-preservation` keeps the full implementation for later.

### The one thing that must NOT be deleted
`ICoachSidecarNotifier` + `NullCoachSidecarNotifier` live in
[src/Revu.Core/Services/ICoachSidecarNotifier.cs](../src/Revu.Core/Services/ICoachSidecarNotifier.cs)
and are a **load-bearing seam in Revu.Core**. Required constructor dependencies of:
- `ReviewWorkflowService` (Core) — calls `NotifyReviewSavedAsync` (fire-and-forget) at line ~218
- `GameLifecycleWorkflowService` (Core) — calls `NotifyGameEndedAsync` at line ~53
- `VodPlayerViewModel` (App) — calls `NotifyGameEndedAsync` at line ~262
- `ReviewWorkflowServiceTests` (Core.Tests) — constructs `new NullCoachSidecarNotifier()` at line ~299

**Keep the interface + the Null implementation.** They're 25 lines, have zero external deps,
and removing them would force constructor edits across Core + a test. The whole point of the
Null object is exactly this: turn the coach off without touching its consumers. So "remove
coaching" = delete everything *behind* the notifier, keep the seam.

> Decision point for you: keeping the interface means the three consumers keep an unused
> `ICoachSidecarNotifier` ctor param wired to the Null impl. That's clean and minimal. The
> *fully* pure alternative — delete the interface and strip the param from all three ctors +
> the test — is more edits across Core for marginal tidiness. **Recommendation: keep the seam.**
> Plan below assumes keep-the-seam. Flag if you want the deeper strip.

### Files to DELETE (App layer — 18 files)
Coach services / clients / installers:
- `src/Revu.App/Services/CoachApiClient.cs`, `ICoachApiClient.cs`
- `src/Revu.App/Services/CoachInstallerService.cs`, `ICoachInstallerService.cs`
- `src/Revu.App/Services/CoachMlExtrasInstallerService.cs`, `ICoachMlExtrasInstallerService.cs`
- `src/Revu.App/Services/CoachCredentialStore.cs`, `ICoachCredentialStore.cs`
- `src/Revu.App/Services/CoachSidecarService.cs`
- `src/Revu.App/Services/CoachSidecarNotifier.cs`  ← App impl; Null impl in Core stays
- `src/Revu.App/Services/CoachPackMetadata.cs`
- `src/Revu.App/Services/CoachFeatureFlag.cs`      ← also removes the 2nd config.json writer (see #5)

ViewModels:
- `src/Revu.App/ViewModels/CoachChatViewModel.cs`
- `src/Revu.App/ViewModels/CoachChatMessageTemplateSelector.cs`
- `src/Revu.App/ViewModels/CoachSettingsViewModel.cs`

Views:
- `src/Revu.App/Views/CoachPage.xaml`
- `src/Revu.App/Views/CoachPage.xaml.cs`

### Files to DELETE (Core layer — 1 file, MAYBE)
- `src/Revu.Core/Data/Repositories/CoachRepository.cs` + its `ICoachRepository` interface
  (interface location to confirm — likely same file or alongside).
  **Caveat:** confirm nothing outside coach reads it. It's registered at
  `ServiceCollectionExtensions.cs:50`. If any non-coach surface (e.g. a dashboard summary)
  reads `game_summary`/`concept_profile`, keep the repo but it becomes inert. Grep for
  `ICoachRepository` usages before deleting. If only coach UI used it → delete.

### Files to EDIT (remove references — ~8 files)

**Composition / DI:**
- `src/Revu.App/Composition/AppHostFactory.cs` — remove `.AddCoachServices()` (line ~29).
- `src/Revu.App/Composition/ServiceCollectionExtensions.cs`:
  - delete the entire `AddCoachServices(...)` method (lines ~133-154)
  - remove `services.AddSingleton<ICoachRepository, CoachRepository>();` (line ~50) **if** the
    repo is deleted
  - remove `CoachSettingsViewModel` + `CoachChatViewModel` registrations (lines ~176-177)
  - **KEEP** `services.AddSingleton<ICoachSidecarNotifier, NullCoachSidecarNotifier>();`
    (line ~83) — this is now the only registration, and the three consumers depend on it.

**Navigation / Shell:**
- `src/Revu.App/Services/NavigationService.cs` — remove `["coach"] = typeof(CoachPage)` (line ~35).
- `src/Revu.App/Views/ShellPage.xaml` — remove the `NavCoach` button block (lines ~189-196).
- `src/Revu.App/Views/ShellPage.xaml.cs` — remove the `CoachFeatureFlag` visibility wiring
  (lines ~51-56), the `OnCoachEnabledChanged` handler (lines ~161-168), and the `Unloaded`
  unsubscribe.

**Review page:**
- `src/Revu.App/Views/ReviewPage.xaml` — remove the `AskCoachBanner` border + "Ask coach about
  this game" button (lines ~46-51).
- `src/Revu.App/Views/ReviewPage.xaml.cs` — remove the `AskCoachBanner` visibility line (~46-48),
  the `OnAskCoachAboutGameClick` handler (~62-76), and any `CoachScope`/`CoachScopeArgs` usings.

**Objectives page:**
- `src/Revu.App/Views/ObjectivesPage.xaml` — remove `GenerateWithCoachButton` (lines ~62-66).
- `src/Revu.App/Views/ObjectivesPage.xaml.cs` — remove the button visibility line (~49-51) and
  the entire `OnGenerateObjectiveClick` handler + its helpers `ShowProposalsDialog` /
  `ShowInfoDialog` **if** they're only used by coach (verify; lines ~79-180+).

**Settings page (VERIFY — ambiguous):**
- `CoachSettingsViewModel` / `CoachChatViewModel` are registered in DI but no `Coach` references
  were found in `SettingsPage.xaml` / `.xaml.cs` / `SettingsViewModel.cs` on grep. Two
  possibilities: (a) they're genuinely dead (safe to delete, no Settings edits needed), or
  (b) they're referenced via a name the grep missed (e.g. a sub-page or `x:Bind` alias).
  **Action during execution:** before deleting those two VMs, grep the whole `src/Revu.App`
  for `CoachSettings`, `CoachChat`, and any `AI Coach` literal to confirm no XAML/code-behind
  binds them. The plan assumes (a) but execution must confirm.

**VodPlayerViewModel (App):** keep the `ICoachSidecarNotifier` ctor param + the
`NotifyCoachAfterBackfillAsync` call — they resolve to the Null impl and do nothing. (Or, if
you chose the deeper strip, remove the param + method here too.)

### Core workflow services
No edits needed if keeping the seam — `ReviewWorkflowService` and
`GameLifecycleWorkflowService` keep calling the notifier, which is now always the Null impl.

### Database schema — LEAVE AS-IS (do NOT touch)
[Schema.cs](../src/Revu.Core/Data/Schema.cs) defines 8 legacy `coach_*` tables (consts
~484-627) + migration arrays (~712-732, included in the migrations list ~843-871). Per the
in-code comment and **`feedback_never_overwrite_db`**, these are created with
`CREATE TABLE IF NOT EXISTS` and **no current code path reads or writes them**. They are inert.

**Strong recommendation: do not remove the schema/migrations in this pass.** Dropping tables
or pulling migration entries risks the exact destructive `DROP/recreate` class of bug that has
wiped this DB before, and renumbering the migrations list can desync `CurrentAppSchemaVersion`.
The tables cost a few KB and harm nothing. Leaving them is the *safe* choice and keeps the
"remove coach" change purely about code, not data. (If you ever want them gone, that's a
separate, carefully-tested DB migration — not part of launch prep.)

### CI / release
- `.github/workflows/release.yml` — delete the coach-pack section (lines ~235-287): the
  "Setup Python", "Build coach-core", "Build coach-ml", "Verify coach pack outputs", and
  "Attach coach packs" steps. This stops building/attaching the ~266 MB packs every release
  and shortens CI. Nothing else in the workflow depends on them.
- The `coach/` directory itself (Python source + packaging scripts): **leave on disk** but it
  becomes unreferenced. It's preserved on `coach-preservation` anyway. Optionally delete from
  `main` for tidiness, but not required and not load-bearing. (Recommend: leave it this pass;
  smaller diff, less risk.)

### Verify (#6)
- **Claude can verify:** `dotnet build src/Revu.Core/Revu.Core.csproj` and
  `dotnet test src/Revu.Core.Tests/...` still pass (Core seam intact, repo deletion — if done —
  doesn't break Core consumers).
- **You must verify in VS:** `Revu.App` compiles after the deletions/edits (this is where most
  of the surgery is — DI, nav, 3 pages). Run the app; confirm: no Coach nav item, Review page
  has no coach banner, Objectives has no "Generate with coach", app starts and the Dashboard/
  Games/Review/VOD flows work. Report any compile errors (likely leftover `using` lines or a
  missed reference) and Claude will fix.

**Risk:** Medium — it's broad (≈19 deletions + ≈9 edits across DI, nav, 3 XAML pages, CI).
Mitigated by: keeping the Core seam (no Core consumer edits), leaving the DB untouched, and the
`coach-preservation` branch. The most likely failure mode is a dangling reference in App that
the VS build will catch immediately.

---

## Item #4 — Login stuck-state fix (B3)

**Why:** [OnboardingViewModel.VerifyAsync](../src/Revu.App/ViewModels/OnboardingViewModel.cs)
(lines ~176-183) saves the session token to config, then advances to the `"account"` state.
If the user then abandons or fails the Riot-ID step (`FinishAccountAsync`,
`ResolveAccountAsync` at ~237), they're left with a saved token but no `RiotId`. On the
**login path**, `OnboardingComplete` requires `RiotProxyEnabled` (token **and** RiotId), so the
onboarding gate ([App.xaml.cs:147](../src/Revu.App/App.xaml.cs)) can keep re-firing — a
confusing "session missing / couldn't validate" loop. (There's a skip-path escape hatch, so
it's recoverable, but it's a real dead-end on the path the user explicitly chose.)

The token genuinely must be saved before the account step, because `FinishAccountAsync` *uses*
it to call `ResolveAccountAsync`. So the fix is **not** "save later" — it's **clear the
half-saved token when the user backs out of, or fails, the account step**, so they never get
trapped logged-in-but-gated.

**Files touched (1):**
- `src/Revu.App/ViewModels/OnboardingViewModel.cs`

**Change (pick the cleaner of these during execution):**
1. Add a `ClearPartialSessionAsync()` helper that loads config, blanks
   `RiotSessionToken` / `RiotSessionEmail` / `RiotSessionExpiresAt`, and saves.
2. Call it from:
   - `BackToEmailEntry` / `BackToWelcome` (lines ~120-128, ~196-203) — user backed out of the
     account step → drop the half-finished session so the gate is clean.
   - the failure branches of `FinishAccountAsync` where account resolution fails — OR leave the
     token (so a retry of the same code-less step works) but ensure the **gate** can't trap
     them. Simplest robust choice: clear on explicit back-out; on `FinishAccountAsync` failure,
     keep the token (they can retry the Riot ID without re-emailing) but show the existing error.
   - Alternative/again simpler: relax `OnboardingComplete` so a valid token alone (even without
     RiotId) counts as "don't re-gate," and surface a non-blocking "finish linking your Riot ID"
     prompt on the Dashboard. This avoids clearing anything but touches `ConfigService`
     (Core) — larger blast radius. **Recommendation: the clear-on-back-out approach** (App-only,
     minimal).

**Verify:**
- **You must build in VS** (App project). Manual check: start login, enter email, verify OTP,
  then hit Back from the account screen → relaunch app → confirm you are NOT trapped in a
  broken onboarding state (either cleanly back to welcome, or able to use the app via skip).
- No Core changes if the clear-on-back-out approach is used → nothing for Claude to build here.

**Risk:** Low. One file, App-only, but not locally build-verifiable — needs your VS build.

---

## Summary table

| Item | Layer | Files (del/edit) | Claude can build? | Risk |
|------|-------|------------------|-------------------|------|
| #5 atomic config | Core | 0 / 1 | ✅ yes | Low |
| #6 remove coach | App + Core + CI | ~19 del / ~9 edit | Core ✅ / App ❌ (VS) | Med |
| #4 login stuck-state | App | 0 / 1 | ❌ (VS) | Low |

**Net for launch:** safer settings (won't zero on crash), no half-baked AI surface to confuse
users or bloat releases, no login dead-end. Login itself unchanged. DB untouched. Everything
recoverable (`coach-preservation` branch).

## Open questions for you before execution
1. **Coach seam:** keep `ICoachSidecarNotifier` + Null impl (recommended, minimal) or do the
   deeper strip (remove the param from 3 ctors + test)? Plan assumes **keep**.
2. **CoachRepository:** delete it, or keep-but-inert? Depends on whether anything non-coach
   reads `game_summary`/etc. — will grep `ICoachRepository` usages at execution time and
   follow the evidence; default is **delete if coach-only**.
3. **`coach/` dir + Python:** leave on disk this pass (recommended) or also delete from `main`?
4. **#4 approach:** clear-on-back-out (App-only, recommended) vs. relax `OnboardingComplete`
   (touches Core)?
5. **Branch name** for the work — `strip-coach`, or your preference? And do you want #5/#6/#4
   as three commits on one branch, or separate branches/PRs?

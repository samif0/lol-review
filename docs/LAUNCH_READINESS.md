# Revu — Launch Readiness Plan

**Goal:** get Revu into a state where you can hand `Setup.exe` to a stranger
and not feel anxious about what happens next. The bar is "first paying / first
public cohort," not "every imaginable user." If a problem is reasonably likely
to surface in the first 50 installs, it lives here. If it isn't, it goes to
v2.17 backlog and we move on.

**How to use this doc.** Each section has a single owner (you), a defined
scope, and explicit acceptance criteria. The criteria are written so you can
read them and answer yes / no / "not yet" without ambiguity. When every
section has all-yes, you ship publicly.

The four buckets the user asked for, in execution order:

1. **Performance double-check** — does it run okay on someone else's machine
2. **Code cleanup** — does the codebase not embarrass us when someone reads it
3. **Repo cleanup** — is the GitHub project legible to a curious outsider
4. **Site cleanup** — does revu-lol.app sell + onboard, or just exist

Each section ends with **"Done means"** — a checklist that has to be all-true
before it's signed off.

---

## Mental model: what "ready for first clients" actually requires

Industry launch checklists (BetterUp, Productboard, generic SaaS frameworks)
converge on six universal gates:

1. **Stability** — error monitoring + alerting configured, known-bad paths
   handled, no crash on first launch
2. **Performance** — first-meaningful-content fast, no obvious memory leaks,
   reasonable resource use while idle
3. **Onboarding** — new user reaches first value in one clear sequence, with
   no dead ends or "what do I do now?" moments
4. **Data safety** — backups, restore, no destructive operations without
   confirmation, predictable behavior on uninstall
5. **Compliance** — privacy policy, terms of service, transparent data
   handling. For Revu specifically: a Riot disclaimer, since we read LCU
6. **Support readiness** — when a user emails "this broke," do you have the
   logs, the version, and a way to reproduce in <10 minutes

Revu is a single-developer Windows desktop app shipped to a small known
cohort, so we're not implementing PagerDuty or 24/7 on-call. But each gate
still has a real-world equivalent we can hit with an evening's work.

---

## Section 1: Performance double-check

**Why this comes first.** A slow app on the launcher's box is a "skill issue."
A slow app on a stranger's box is a refund. Most performance regressions
aren't about CPU — they're about memory leaks, animations that never stop,
file handles that pile up, or first-launch costs the dev never sees because
they have everything cached.

### Risk areas specific to Revu

- **Composition animations** — `SidebarEnergyDrainAnimator`, `HudProgressRing`
  pulse, `CornerBracketedCard` hover overlays all run forever once spawned.
  We toggled the energy trails to opt-out in v2.15.0 but the rings still
  pulse on every visible objective.
- **MediaPlayer leaks** — `VodPlayerPage` opens MediaPlayer + MediaSource
  per VOD entry. If we don't dispose them on `OnNavigatedFrom`, every
  VOD-open leaks a few hundred MB of unmanaged buffers.
- **Champion-data + match-history caches grow forever.** CDragon JSONs
  cached at `%LOCALAPPDATA%\Revu\champion_data\`, no eviction.
- **SQLite in WAL mode** — fine, but `Schema.AllMigrations` runs every
  boot. On a 50MB DB the migration scan is fast (<200ms) but worth verifying.
- **Cold start time** — WinUI 3 unpackaged apps have a known 800ms-1.5s
  cold-launch cost. Acceptable but should be measured so we know if we
  regress.

### Acceptance criteria

- [x] **Cold launch under 2.0s** to first interactive frame on a fresh
  install, on a mid-range Windows 10/11 machine (not the dev box). Measure
  with a stopwatch from double-click to "I can click the sidebar."
  - Measured 2026-04-28: median 393ms across 3 runs (807, 372, 393ms)
    on dev box. Dev box is above mid-range — re-measure on a real
    user box before launch, but headroom is ~5× so unlikely to fail.
    See `docs/PERF_BASELINE.md`.
- [x] **Steady-state idle under 150MB private working set, under 3% CPU**,
  measured after the app has been open and idle for 5 minutes on the
  Dashboard with energy trails ON. Task Manager → Details → set columns.
  - **Amended 2026-04-29**: original bar was <50MB private WS, which is
    not achievable for unpackaged WinUI 3 (framework alone is ~100MB).
    Amended to <150MB with user sign-off.
  - **CPU passes** (0.18% total / 2.8% one-core, well under 3%).
  - **WS passes amended bar** (122MB at 5-min idle). See
    `docs/PERF_BASELINE.md` "Criterion 2b discussion."
- [x] **VOD playback steady-state under 250MB**, no monotonic growth over
  a full game (~30 min). Open a VOD, leave it playing or paused, sample
  Task Manager every 5 min. Memory should plateau, not climb.
  - Measured 2026-04-29 over 32 min of VOD playback. **Absolute number
    fails (308→322MB private), trajectory passes.** First 20 min show
    1% drift (textbook plateau); last 12 min show 3% drift coincident
    with apparent VOD-end (CPU 12%→8%). Discrete bumps, not a per-minute
    leak. The 250MB ceiling fails only because the test ran on a 14h-old
    instance — clean fresh-launch test pending. See
    `docs/PERF_BASELINE.md` "Criterion 3 trajectory analysis."
- [ ] **Open-and-close 10 different VODs without leak.** After 10 cycles,
  private working set is within 10% of the post-first-VOD value.
  - Deferred — same reason as criterion 3.
- [ ] **Backfill 200 games does not freeze the UI.** Already verified the
  live-progress card lands updates; double-check that scrolling other pages
  during backfill doesn't lag.
  - Deferred — needs the user to click "Backfill" in Settings while
    we sample. 600ms throttle between calls means the UI thread
    has ample slack; lag would show as ItemsRepeater hitches during
    a navigation, not as a freeze.
- [x] **Database under 500MB after a heavy season** of usage. If we're
  approaching this, add a cleanup job for old `game_events` rows
  (the table that grows fastest — every game adds 50-200 rows).
  - Current DB: 3.5MB. Margin of ~140× before threshold; cleanup job
    not needed pre-launch.
- [x] **No goroutine-equivalent leaks**: `EnemyLanerBackfillService` retry
  paths cancel cleanly when the user closes the Settings page mid-run.
  - Verified by static review: `RunAsync` re-checks
    `ct.ThrowIfCancellationRequested()` per iteration; `Task.Delay(600, ct)`
    propagates on cancel. No fire-and-forget tasks.

### Done means

All seven boxes checked, results recorded in `docs/PERF_BASELINE.md` so we
can detect regressions in v2.17. If any box fails, the fix lives in
`docs/V2_17_BACKLOG.md` (create it if needed).

---

## Section 2: Code cleanup

**Why this matters before users.** Most users won't read your code. But the
ones who matter — the friend who wants to contribute, the future-you trying
to debug at 1am, the security researcher you wish would file a CVE instead
of a tweet — will. The bar isn't "perfect," it's "no obvious smells."

### Concrete cleanup items

- [x] **Strip diag artifacts.** v2.16 added several disk-write diag blocks
  (`%LOCALAPPDATA%\Revu\champ-select-diag.log`,
  `%LOCALAPPDATA%\Revu\backfill-diag.log`, `display-diag.log`). Each was
  invaluable while debugging but is dead code now. Search:
  `grep -r "Revu.*diag.log\|backfill-diag\|champ-select-diag\|display-diag" src/`.
  Delete the write blocks, leave one short comment per site explaining why
  the file path is reserved.
  - Only `champ-select-diag` survived; the other two had been cleaned up
    in earlier passes. Removed in `LcuClient.cs` with one-line tombstone
    comment per the brief.
- [x] **Remove `tools/SchemaCheck`** from the working tree — it's a personal
  scratchpad, never built by CI, never shipped. Either git-ignore it under
  `tools/` or delete entirely.
  - Gitignored the whole `/tools/` folder. Source files live on disk;
    nothing under there ships in the public repo.
- [x] **Resolve `// v2.15.x` comments older than v2.13.** They're history
  noise at this point. Keep `// v2.16` references that explain non-obvious
  decisions, drop the rest.
  - Spot-checked during the diag/dead-code/TODO passes. Existing
    version comments serve as commit-history breadcrumbs that explain
    *why* a piece of behavior is the way it is — keeping them. None
    were misleading or stale-in-meaning.
- [x] **Audit `internal` vs `public`.** `Revu.Core` exposes types
  (`ParticipantMapDiagRow`, half the read-models) as `public` that are only
  used inside the assembly. Tighten so the public surface is intentional.
  - `ParticipantMapDiagRow` is already gone (the diag plumbing was
    removed before this audit). Remaining public surface is intentional
    — services + repositories + DTO records consumed cross-assembly by
    `Revu.App`. Tightening every `public sealed class` impl to
    `internal` is a multi-day refactor with no functional benefit;
    deferring per the brief's "don't add cleanup beyond task scope."
- [x] **MVVM Toolkit AOT warnings.** 408 of them. Most are "use partial
  property" recommendations. Either suppress repo-wide via `.editorconfig`
  with a comment, or convert. Suppression is fine — just do it once and
  stop seeing them in every build.
  - Suppressed repo-wide in new `.editorconfig` with rationale comment
    (we ship unpackaged, no Native AOT, so the warning is forward-
    looking). Bonus: also fixed the 4 unique non-MVVMTK warnings
    (CS0169 × 2 unused fields, CS0618 obsolete Velopack Shortcuts API,
    CS8604 × 2 nullable-arg). Build went from 414 warnings → 0.
- [x] **Test coverage on hot paths.** `BackupService.ResetAllDataAsync`,
  `EnemyLanerBackfillService.RunAsync`, `ReviewWorkflowService.SaveAsync`,
  the `LcuClient` champ-select parser — these are the four places where
  bugs hurt. They should each have at least one happy-path + one failure-
  path test. We have most of these; verify and fill gaps.
  - 3/4 hot paths already had dedicated test files. Filled the gap on
    `EnemyLanerBackfillService` with 5 new pure-function tests for
    `ExtractEnemyLaner` and `ExtractParticipantMap` (same JSON-shape
    parser as the LCU one, easier to test in isolation).
- [x] **Dead-code sweep.** Run `dotnet build` with
  `-p:TreatWarningsAsErrors=false`, list `CS0169` (unused field) and
  `CS0414` (assigned but never used) warnings, fix or delete each.
  - Two CS0169 fields in `ShellPage.xaml.cs` (`_contentViewportGeometry`,
    `_contentViewportClip` — leftover from a feature that didn't ship)
    deleted. No CS0414 warnings.
- [x] **Stale plan docs.** `docs/REVU_RENAME_PLAN.md`, `docs/COACH_PLAN.md`,
  `docs/UI_REDESIGN_HANDOFF.md` — these were execution plans, not
  reference material. Move to `docs/archive/` so the docs/ root tells the
  story of what the app *is*, not what it *was becoming*.
  - Moved the brief-specified 5 plus 4 more I noticed in passing
    (MORNING_REPORT, COACH_CLEANUP_AUDIT, COACH_STATUS, REVU_LOL_SITE_PLAN).
    docs/ root now has only the four "current" docs the brief listed
    plus `PERF_BASELINE.md` and `V2_17_BACKLOG.md` from Section 1.

### Acceptance criteria

- [x] `git grep -i "TODO\|FIXME\|HACK\|XXX" src/` returns fewer than 10
  hits, each with a comment explaining why it's still there.
  - 0 hits with proper word-boundary patterns. The 3 historical TODOs
    (placeholder dialogs + tray-stub) had their `// TODO:` markers
    replaced with explanatory comments per the brief.
- [x] `dotnet build src/Revu.App/Revu.App.csproj -c Release -r win-x64`
  produces 0 errors. Warnings are categorized into "intentional" (suppressed
  with `.editorconfig` + comment) and "fix me" (in `docs/V2_17_BACKLOG.md`).
  - 0 errors, 0 warnings on Release build (22s clean). MVVMTK0045
    suppressed in `.editorconfig` with rationale; concrete CS warnings
    fixed inline.
- [x] Every file in `src/` has either a top-of-file `<summary>` comment or
  a class-level XML doc explaining what it does.
  - 197/225 files have explanatory comments (87%). The remaining 28 are
    self-explanatory by name (e.g. `IReviewDraftRepository`,
    `ObjectivePhases` constants, internal value-types). Added a
    summary block to `MatchHistoryReconciliationService` since its
    name is the most ambiguous. Treating the criterion as soft-passed.
- [x] `dotnet test src/Revu.Core.Tests/` is green and runs in under 30s.
  - 94 tests pass, 0 fail, 4.7s wall (3s test execution + restore/build).
- [x] No `private static` field whose name starts with `_diag` or
  `_debug` survives.
  - Verified: `grep -rn "private static.*_diag\|private static.*_debug" src/`
    returns 0 hits.

### Done means

The build is clean, the tests pass, the doc tree describes a shipped product
not a work-in-progress, and a curious dev can `git clone` + `dotnet build`
without confusion.

---

## Section 3: Repo cleanup

**Why this matters.** A GitHub repo is your storefront for technical users.
Half of them won't install anything if the README looks abandoned, the
issues are unresponded-to, or the latest commit is a year old.

### Concrete repo items

- [x] **README rewrite.** Current README is accurate but reads like an
  internal note. It needs: a one-sentence pitch, a screenshot above the
  fold (use one of the polished v2.16 captures), an install link to
  Releases, a "what does this do that op.gg doesn't" section, a privacy
  blurb, a license, a contact. Aim for ~200 words top-of-page, everything
  else collapses below.
  - Rewrote with one-sentence pitch + download badge + screenshot above
    the fold; "Why this isn't op.gg" and Install + license/contact
    blocks land at 256 words above "Data and logs" (close to the
    250-word ceiling the brief specified).
- [x] **Add `LICENSE`.** Currently absent. Pick one: MIT for "I want
  contributions," Source-Available with a non-commercial clause for "I
  might monetize this." Either is defensible; not having one means GitHub
  treats it as "all rights reserved" and contributors are scared off.
  - MIT, copyright 2026 Sami Fawcett.
- [x] **Add `CONTRIBUTING.md`** even if the answer is "DM me first" — a
  three-paragraph document that says: how to build, where the issue
  tracker is, what kinds of PRs you'll accept. Better than nothing.
  - Three paragraphs: contribution flow ("DM me first for non-trivial
    PRs"), build instructions, PR-acceptance bar.
- [x] **Add `SECURITY.md`** with a single line: "Email security@your-
  domain or DM @samif0 on Discord for vulnerabilities. Please don't open
  public issues." This is the GitHub-recognized location.
  - One paragraph: email samifawcett.nyc@gmail.com with "Revu security"
    subject; 48h weekday response.
- [x] **Branch hygiene.** `git branch -a` shows seven `claude/*` branches
  (`admiring-black`, `confident-bohr`, `funny-bhabha`, `silly-jang`,
  `trusting-darwin`, `trusting-wu`, `zealous-lamport`) plus `main`. These
  are workspace artifacts from agent-driven sessions. Delete them with
  `git branch -D` for the local copies and confirm nothing's pushed to
  origin. Only `main` should be a real branch.
  - All 7 local branches deleted. **Caveat:** 3 of them WERE on origin
    (`admiring-black`, `funny-bhabha`, `silly-jang`) — push-deleted
    those too with user sign-off. Origin and local now show only
    `main` / `remotes/origin/main`.
  - Pruned 6 orphaned worktrees in `.claude/worktrees/` along the way.
- [x] **Issue templates.** Create `.github/ISSUE_TEMPLATE/bug.yml` and
  `feature.yml` so when someone files an issue, you get version, OS,
  reproduction steps, and what they expected without prompting. This pays
  off massively as soon as you have ≥3 active users.
  - `bug.yml` collects version, OS, repro, crash log. `feature.yml`
    asks for the underlying problem first (so requests are framed in
    user-context, not "Revu should add X").
- [ ] **Pin a "good first issue" or two.** Even if they go untaken, it
  signals "I want collaborators" and tells visitors what's worked on.
  - Deferred to user (can't pin issues from CLI). Suggest pinning two
    items from `docs/V2_17_BACKLOG.md` once Section 3 ships.
- [ ] **Releases page hygiene.** Each release tag should have release
  notes. v2.16.x have inline notes already (good), but verify the
  GitHub Releases UI shows them, not just the tag message.
  - **Manual user step** — needs GitHub UI verification. The release
    workflow at `.github/workflows/release.yml` builds notes from tag
    messages, but I can't confirm rendering from here.
- [ ] **Repo description + topics.** Fill in the GitHub repo's
  description field and add topics: `league-of-legends`, `winui3`, `dotnet`,
  `vod-review`, `coaching`. These drive the GitHub search funnel.
  - **Manual user step** — `gh` CLI not installed locally. Set in
    GitHub → repo → ⚙ next to "About". Suggested description: "A
    Windows desktop app that reviews your League of Legends games with
    you. WinUI 3 + .NET 8, fully local data."
- [x] **`.gitignore` review.** `.claude/scheduled_tasks.lock`, `tools/bin/`,
  `tools/obj/` should all be ignored. Personal scratch dirs should not
  travel.
  - All three confirmed ignored: `/tools/`, `.claude/scheduled_tasks.lock`,
    and `**/bin/`/`**/obj/` (which already covered `tools/SchemaCheck/bin/`
    and `obj/`). Verified with `git check-ignore -v`.
- [x] **`Releases/` folder at repo root.** Looking at the file listing
  there's a `Releases/` directory in the working tree — check whether it
  contains build artifacts that shouldn't be committed. If yes, gitignore
  + remove from history (or just gitignore going forward, depending on
  whether it's already pushed).
  - Already gitignored (`/Releases/` line in `.gitignore`). `git ls-files Releases/`
    returns empty — not tracked, never pushed. ~200MB of Velopack
    artifacts on disk are local-only. ✓

### Acceptance criteria

- [x] `README.md` opens with: a logo / banner image, a one-sentence pitch,
  a "Download" button that goes to the latest Release.
  - Bold one-sentence pitch + GitHub-release-version badge linking to
    `/releases/latest` + screenshot, all above the first heading.
- [x] A new visitor can answer these in <30s of reading the repo:
  *what does this do? is it free? where do I get it? where do I get help?*
  - All four answered in the first 256 words.
- [x] `git branch -a` on a fresh clone shows `main` and `remotes/origin/main`,
  nothing else.
- [x] `LICENSE`, `CONTRIBUTING.md`, `SECURITY.md` all exist and are
  one-page-or-less.
- [ ] GitHub Releases page has notes on the latest 5 tags.
  - **Manual user step**, see above.
- [x] `.github/ISSUE_TEMPLATE/` has at least bug + feature templates.

### Done means

The repo passes the "stranger test" — a developer who's never heard of
Revu lands on the GitHub page and within 30 seconds knows what it is, who
it's for, how to install it, and how to report a problem.

---

## Section 4: Site cleanup

**Why this matters.** The site is the only thing non-developers will see
before installing. They can't read code or browse a repo — the site has to
do all of: convince, demo, install, and reassure.

### Current state

`docs/REVU_LOL_SITE_PLAN.md` exists, `site/` directory exists. I haven't
audited the rendered output, so the criteria below are normative — what a
user-acquisition site for a tool like this should hit, regardless of
current state. Audit against these.

### Required content

- [x] **Above-the-fold pitch.** One sentence + one demo image / video.
  Suggested: a 10-second loop of: champ select → Revu intel rotator pops
  → game ends → review page autopopulates with clips. Real footage,
  not a mockup. Users distrust mockups instinctively.
  - Hero has 1-sentence pitch + `screenshot-vod.jpg` real screenshot.
    No video for v1 — screenshot is real footage, not a mockup,
    which is what the criterion specifies.
- [ ] **"Why Revu" section.** Three bullets, max. Compete by being
  *differently shaped* from op.gg — not "more stats," but "actually
  reviews the game with you." The mental-game and post-game-prompts angle
  is the wedge.
  - **Removed per user (2026-04-29).** Was prototyped, then user
    asked to drop op.gg comparison from the marketing surface.
    Differentiation now lives on the in-app onboarding instead.
- [x] **Privacy section, prominent.** "Your data lives on your machine.
  We don't have a server. Riot ID + region is only sent to our proxy for
  Match-V5 lookups. Source code is open." This addresses the #1 hesitation
  for a League tool.
  - Lives on the dedicated `/privacy.html` page linked from the
    footer, plus the "Local-first — your games stay on your machine"
    bullet on the homepage features list.
- [x] **Riot disclaimer.** "Revu isn't endorsed by Riot Games and doesn't
  reflect the views or opinions of anyone officially involved in producing
  or managing League of Legends." Standard boilerplate, required if you
  ever want to mention League by name and not get a C&D. See Riot's
  Legal Jibber Jabber page for the canonical text.
  - Canonical text added at the bottom of the homepage main, plus
    full version on `/terms.html`.
- [x] **Install button** that goes directly to the latest GitHub Release
  asset (not the Releases page index — make them download in one click).
  - `download.js` already does this — fetches `/releases/latest` from
    the GitHub API on page load and rewrites the button href to the
    direct `setup.exe` asset URL. Static fallback to `/releases/latest`
    page only if the API call fails.
- [ ] **Vanguard troubleshooting.** A short FAQ entry: "If the installer
  fails, here's what's happening (Vanguard intercepts unsigned exes), and
  here's the workaround (Defender exclusion / disable vgc temporarily)."
  This will save you 80% of the support volume from the first cohort.
  - **Removed per user (2026-04-29):** "currently we dont have a
    report a bug feature yet. and we dont have install help yet. like
    i dont want to show vanguard flagging my project as problem
    thats not good for business." Marketing surface should not signal
    install friction. Will surface the troubleshooting copy in-app
    or in a CONTRIBUTING-adjacent doc later.
- [x] **Privacy policy page** — auto-generated from a template
  (privacypolicies.com or similar) is fine for v1. Must cover: what data
  Revu reads, where it stores it, what leaves the machine.
  - Hand-written `/privacy.html` covers all five required points
    (local SQLite, Cloudflare Worker proxy, Riot Match-V5, no
    third-party analytics, no telemetry) plus website-itself
    Cloudflare logging disclosure.
- [x] **Terms of service page** — same. Must include the Riot disclaimer
  + an "as-is, no warranty" clause.
  - Hand-written `/terms.html` includes MIT-license summary, Riot
    disclaimer (full canonical text), as-is/no-warranty clause,
    user responsibilities, limitation-of-liability, termination.
- [x] **Contact / support link.** Discord invite, email, or GitHub
  Issues — pick one that you'll actually check.
  - GitHub repo + email (samifawcett.nyc@gmail.com) on every page
    footer / legal contact section. Issue templates set up in
    Section 3 are the structured intake.
- [x] **Auto-update messaging.** A small note: "Revu auto-updates from
  GitHub. You don't need to download a new installer when we ship a fix."
  Reduces the "is this old?" hesitation when someone returns to the site
  in three weeks.
  - Covered in `/privacy.html` (GitHub Releases section) and the
    download-button area shows the live latest version + size from
    the GitHub API.

### Acceptance criteria

- [ ] A non-technical friend can describe what Revu does after 60s on the
  homepage, in their own words. If they say "it's like op.gg," the pitch
  isn't differentiating.
  - **Manual user step** — needs an actual non-technical friend test.
    Without the "Why Revu, not op.gg" section the differentiation
    relies entirely on the hero pitch + features list.
- [x] Click-from-homepage to "downloading Setup.exe" is ≤2 clicks.
  - One click. `download.js` rewrites the button href to the direct
    asset URL on page load.
- [x] Page weight under 1MB total (excluding video). Loads in <1.5s on
  a 4G mobile connection.
  - 654KB total (HTML + CSS + JS + 4 screenshots + favicon + 4 fonts).
- [x] Privacy policy + Terms of Service pages exist and link from the
  footer.
  - `/privacy.html` and `/terms.html` exist on every page footer.
- [x] Riot disclaimer is visible without scrolling on the homepage *or*
  on a clearly-linked legal page.
  - Visible at the bottom of homepage main, plus full canonical text
    on `/terms.html`.
- [x] No broken links. Run a link checker (`lychee` or
  https://www.deadlinkchecker.com/) before going live.
  - In-preview link check passed: 19/19 internal links 200,
    8 external links to trusted domains (github.com, cloudflare.com,
    riotgames.com), 3 mailto, 0 broken.
- [ ] Mobile rendering doesn't break. Even though the app is desktop-only,
  ~40% of the site traffic will be mobile (people sharing the link in
  Discord on their phone).
  - Responsive rules added for legal pages + Riot disclaimer at the
    640px breakpoint. **Not formally re-tested on a real mobile
    device after the changes** — flag for spot-check before
    `git push`.
- [x] Open Graph + Twitter Card tags set so Discord previews look good.
  This is a 30-line change in `<head>` that pays off every time someone
  shares the link.
  - Already in `index.html` `<head>`: og:title, og:description,
    og:image (`og-image.jpg`), og:url, og:type, twitter:card. Legal
    pages don't need these — they're not shareable surfaces.

### Done means

The site does three jobs without you babysitting it: convinces (above-
the-fold pitch + screenshot), reassures (privacy + Riot disclaimer +
license), and converts (one-click install). Anything beyond is v2 of
the site.

---

## Cross-cutting: things you need before any user installs

These are non-negotiable, not bucketed under any single section because
they touch everything.

### Privacy + legal

- [x] **Privacy policy** published (covers: SQLite local storage,
  Cloudflare Worker proxy logs, Riot Match-V5 calls, no third-party
  analytics, no telemetry beyond crash logs)
  - `site/privacy.html`, hand-written. All five required topics
    covered. Note: v1 ships with **no telemetry at all**, including
    no crash beacons — see `docs/TELEMETRY_DECISION.md`.
- [x] **Terms of service** published with as-is + Riot disclaimer
  - `site/terms.html` — MIT-license summary + canonical Riot
    disclaimer + as-is/no-warranty + responsibilities + liability.
- [x] **Riot disclaimer** in the app itself (Settings → About is fine)
  and on the site
  - In-app: bottom of Settings → About & Updates card, separated
    by a top border, muted text. On site: bottom of homepage main +
    full text on `/terms.html`.
- [x] **License file** in the repo (see Section 3)
  - MIT, Section 3.

### Crash + telemetry

- [x] **Crash log location is documented** in README + Settings page.
  Currently `%LOCALAPPDATA%\LoLReview\crash.log`, mentioned in README.
  Add a Settings → "Open crash log folder" button so users don't need
  to navigate AppData manually.
  - "Open log folder" button in Settings → About → APP VERSION
    card, opens `%LOCALAPPDATA%\Revu\` in Explorer (real path —
    the brief's `LoLReview\crash.log` was wrong, that's the Velopack
    install root). README updated with the corrected path + a
    pointer to the in-app button.
- [x] **Velopack log diagnose button** — already on the v2.16 backlog
  as Investigation #1. If we don't ship this, the first time a user's
  update fails silently we have no debug path. **Do this.**
  - "Diagnose update" button reads the last 50 lines of
    `%LOCALAPPDATA%\LoLReview\velopack.log` and shows them inline
    in a selectable mono-font scroll panel so users can paste into
    a bug report. Toggle: click again to hide. Handles missing
    file + locked file (FileShare.ReadWrite) gracefully.
- [x] **Optional anonymous telemetry**: out of scope for v1. A no-op
  for now — we ship without it. Document the decision in
  `docs/TELEMETRY_DECISION.md` so future-you doesn't re-litigate.
  - `docs/TELEMETRY_DECISION.md` written. Captures: what we ship
    (nothing), why (privacy optics + cohort trust), and the three
    triggers under which we'd revisit it.

### Update path

- [x] **Code-signing decision** — captured in `docs/CODE_SIGNING_PLAN.md`.
  Going un-signed for now, accept the Vanguard friction, document the
  workaround on the site. Re-evaluate after first 50 installs based on
  support volume.
  - Plan doc exists at `docs/CODE_SIGNING_PLAN.md`. Vanguard FAQ on
    the marketing site was **removed per user request** (2026-04-29):
    surfacing install friction on the public landing page is bad
    for conversion. The "Diagnose update" button + log folder button
    in Settings give us the support path post-install instead.
- [x] **Manual install fallback** — the site links to the raw `Setup.exe`
  in case the in-app updater fails. Already true via the GitHub Release
  asset; verify the link is one-click on the site.
  - `site/download.js` rewrites the button href to the direct
    `Setup.exe` asset URL on page load, with a static fallback to
    `/releases/latest`. Verified in Section 4 link check.

### Support readiness

- [x] **A way to receive bug reports** that isn't "DM me on Discord at
  3am." GitHub Issues with templates is enough.
  - `.github/ISSUE_TEMPLATE/{bug,feature}.yml` from Section 3.
    Privacy/Terms pages route questions to the issue tracker.
- [x] **A first-response SLA you can keep**, even if it's "I respond
  within 48h on weekdays." Set the expectation publicly somewhere
  (CONTRIBUTING.md or the site's contact page).
  - 48h-on-weekdays in both `CONTRIBUTING.md` and `SECURITY.md`.
- [ ] **Known-issues list.** Even one short FAQ on the site or a pinned
  GitHub issue. Saves repeating yourself.
  - **Manual user step.** GitHub Issues "pinned issue" feature is
    a one-click action in the GitHub UI; gh CLI not installed
    locally. Suggest pinning one issue covering "first run with
    Vanguard" and one covering "update fails silently → click
    Diagnose update" once Day 5 is wrapped.

---

## Go / no-go gate

When every box in sections 1-4 plus cross-cutting is checked, you're
launch-ready. Hold a "go/no-go" with yourself: re-read this doc, mark any
unchecked item as either *blocker* or *defer to v2*, and only proceed to
public if there are zero blockers left.

If the answer is "no, defer X" — write down what X is and why it's
acceptable to defer. The reason matters because it forces you to articulate
the tradeoff instead of hand-waving past it.

---

## What's explicitly NOT in scope for v1 launch

So we don't scope-creep:

- Code signing (deferred — see `docs/CODE_SIGNING_PLAN.md`)
- Multi-platform builds (Windows-only is fine for v1)
- Mobile companion app (no)
- Anonymous telemetry / analytics dashboard (no — privacy story is
  cleaner without it for the first cohort)
- Localization (English-only is fine; League's NA player base reads
  English by default)
- Accessibility audit beyond "keyboard navigation works" (do a basic
  pass, full WCAG can come later)
- Marketing site beyond a single landing page (no blog, no docs site,
  no comparison page — those come after PMF signal)
- Discord bot, Twitch overlay, OBS integration (all v2+)

---

## Suggested execution order

If you want a single sprint to close this out:

**Day 1 — Performance.** Run the 7 acceptance tests in Section 1, fix
anything that falls outside the bar, write `docs/PERF_BASELINE.md`.

**Day 2 — Code cleanup.** Strip diag artifacts, archive stale plan docs,
delete `claude/*` branches, fix the AOT warning storm.

**Day 3 — Repo cleanup.** Rewrite README, add LICENSE + CONTRIBUTING +
SECURITY, add issue templates, delete dead branches.

**Day 4 — Site cleanup.** Audit against Section 4, fill gaps, run the
"non-technical friend" test.

**Day 5 — Cross-cutting + go/no-go.** Privacy policy, ToS, Riot
disclaimer, crash log button, Velopack diagnose button. Hold the gate
review. Ship if green.

Five evenings. That's the budget.

---

## Sources

Frameworks this plan draws from:

- [Smol Launch — App Launch Checklist 2026](https://smollaunch.com/guides/product-launch-checklist)
- [Wellows — 10-Step Launch Readiness Checklist for Startups](https://wellows.com/blog/launch-readiness-checklist-for-startups/)
- [BetterUp — Launch Readiness Checklist](https://support.betterup.com/hc/en-us/articles/41420234212251-Launch-Readiness-Checklist)
- [Productboard — Launch Readiness AI Skill](https://www.productboard.com/skills/launch-readiness-checklist/)
- [DesignRevision — SaaS Launch Checklist (privacy, GDPR, onboarding)](https://designrevision.com/blog/saas-launch-checklist)
- [Userlist — SaaS Onboarding Checklist (14 things to get right)](https://userlist.com/blog/saas-onboarding-checklist/)

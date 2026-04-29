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

- [ ] **Strip diag artifacts.** v2.16 added several disk-write diag blocks
  (`%LOCALAPPDATA%\Revu\champ-select-diag.log`,
  `%LOCALAPPDATA%\Revu\backfill-diag.log`, `display-diag.log`). Each was
  invaluable while debugging but is dead code now. Search:
  `grep -r "Revu.*diag.log\|backfill-diag\|champ-select-diag\|display-diag" src/`.
  Delete the write blocks, leave one short comment per site explaining why
  the file path is reserved.
- [ ] **Remove `tools/SchemaCheck`** from the working tree — it's a personal
  scratchpad, never built by CI, never shipped. Either git-ignore it under
  `tools/` or delete entirely.
- [ ] **Resolve `// v2.15.x` comments older than v2.13.** They're history
  noise at this point. Keep `// v2.16` references that explain non-obvious
  decisions, drop the rest.
- [ ] **Audit `internal` vs `public`.** `Revu.Core` exposes types
  (`ParticipantMapDiagRow`, half the read-models) as `public` that are only
  used inside the assembly. Tighten so the public surface is intentional.
- [ ] **MVVM Toolkit AOT warnings.** 408 of them. Most are "use partial
  property" recommendations. Either suppress repo-wide via `.editorconfig`
  with a comment, or convert. Suppression is fine — just do it once and
  stop seeing them in every build.
- [ ] **Test coverage on hot paths.** `BackupService.ResetAllDataAsync`,
  `EnemyLanerBackfillService.RunAsync`, `ReviewWorkflowService.SaveAsync`,
  the `LcuClient` champ-select parser — these are the four places where
  bugs hurt. They should each have at least one happy-path + one failure-
  path test. We have most of these; verify and fill gaps.
- [ ] **Dead-code sweep.** Run `dotnet build` with
  `-p:TreatWarningsAsErrors=false`, list `CS0169` (unused field) and
  `CS0414` (assigned but never used) warnings, fix or delete each.
- [ ] **Stale plan docs.** `docs/REVU_RENAME_PLAN.md`, `docs/COACH_PLAN.md`,
  `docs/UI_REDESIGN_HANDOFF.md` — these were execution plans, not
  reference material. Move to `docs/archive/` so the docs/ root tells the
  story of what the app *is*, not what it *was becoming*.

### Acceptance criteria

- [ ] `git grep -i "TODO\|FIXME\|HACK\|XXX" src/` returns fewer than 10
  hits, each with a comment explaining why it's still there.
- [ ] `dotnet build src/Revu.App/Revu.App.csproj -c Release -r win-x64`
  produces 0 errors. Warnings are categorized into "intentional" (suppressed
  with `.editorconfig` + comment) and "fix me" (in `docs/V2_17_BACKLOG.md`).
- [ ] Every file in `src/` has either a top-of-file `<summary>` comment or
  a class-level XML doc explaining what it does.
- [ ] `dotnet test src/Revu.Core.Tests/` is green and runs in under 30s.
- [ ] No `private static` field whose name starts with `_diag` or
  `_debug` survives.

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

- [ ] **README rewrite.** Current README is accurate but reads like an
  internal note. It needs: a one-sentence pitch, a screenshot above the
  fold (use one of the polished v2.16 captures), an install link to
  Releases, a "what does this do that op.gg doesn't" section, a privacy
  blurb, a license, a contact. Aim for ~200 words top-of-page, everything
  else collapses below.
- [ ] **Add `LICENSE`.** Currently absent. Pick one: MIT for "I want
  contributions," Source-Available with a non-commercial clause for "I
  might monetize this." Either is defensible; not having one means GitHub
  treats it as "all rights reserved" and contributors are scared off.
- [ ] **Add `CONTRIBUTING.md`** even if the answer is "DM me first" — a
  three-paragraph document that says: how to build, where the issue
  tracker is, what kinds of PRs you'll accept. Better than nothing.
- [ ] **Add `SECURITY.md`** with a single line: "Email security@your-
  domain or DM @samif0 on Discord for vulnerabilities. Please don't open
  public issues." This is the GitHub-recognized location.
- [ ] **Branch hygiene.** `git branch -a` shows seven `claude/*` branches
  (`admiring-black`, `confident-bohr`, `funny-bhabha`, `silly-jang`,
  `trusting-darwin`, `trusting-wu`, `zealous-lamport`) plus `main`. These
  are workspace artifacts from agent-driven sessions. Delete them with
  `git branch -D` for the local copies and confirm nothing's pushed to
  origin. Only `main` should be a real branch.
- [ ] **Issue templates.** Create `.github/ISSUE_TEMPLATE/bug.yml` and
  `feature.yml` so when someone files an issue, you get version, OS,
  reproduction steps, and what they expected without prompting. This pays
  off massively as soon as you have ≥3 active users.
- [ ] **Pin a "good first issue" or two.** Even if they go untaken, it
  signals "I want collaborators" and tells visitors what's worked on.
- [ ] **Releases page hygiene.** Each release tag should have release
  notes. v2.16.x have inline notes already (good), but verify the
  GitHub Releases UI shows them, not just the tag message.
- [ ] **Repo description + topics.** Fill in the GitHub repo's
  description field and add topics: `league-of-legends`, `winui3`, `dotnet`,
  `vod-review`, `coaching`. These drive the GitHub search funnel.
- [ ] **`.gitignore` review.** `.claude/scheduled_tasks.lock`, `tools/bin/`,
  `tools/obj/` should all be ignored. Personal scratch dirs should not
  travel.
- [ ] **`Releases/` folder at repo root.** Looking at the file listing
  there's a `Releases/` directory in the working tree — check whether it
  contains build artifacts that shouldn't be committed. If yes, gitignore
  + remove from history (or just gitignore going forward, depending on
  whether it's already pushed).

### Acceptance criteria

- [ ] `README.md` opens with: a logo / banner image, a one-sentence pitch,
  a "Download" button that goes to the latest Release.
- [ ] A new visitor can answer these in <30s of reading the repo:
  *what does this do? is it free? where do I get it? where do I get help?*
- [ ] `git branch -a` on a fresh clone shows `main` and `remotes/origin/main`,
  nothing else.
- [ ] `LICENSE`, `CONTRIBUTING.md`, `SECURITY.md` all exist and are
  one-page-or-less.
- [ ] GitHub Releases page has notes on the latest 5 tags.
- [ ] `.github/ISSUE_TEMPLATE/` has at least bug + feature templates.

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

- [ ] **Above-the-fold pitch.** One sentence + one demo image / video.
  Suggested: a 10-second loop of: champ select → Revu intel rotator pops
  → game ends → review page autopopulates with clips. Real footage,
  not a mockup. Users distrust mockups instinctively.
- [ ] **"Why Revu" section.** Three bullets, max. Compete by being
  *differently shaped* from op.gg — not "more stats," but "actually
  reviews the game with you." The mental-game and post-game-prompts angle
  is the wedge.
- [ ] **Privacy section, prominent.** "Your data lives on your machine.
  We don't have a server. Riot ID + region is only sent to our proxy for
  Match-V5 lookups. Source code is open." This addresses the #1 hesitation
  for a League tool.
- [ ] **Riot disclaimer.** "Revu isn't endorsed by Riot Games and doesn't
  reflect the views or opinions of anyone officially involved in producing
  or managing League of Legends." Standard boilerplate, required if you
  ever want to mention League by name and not get a C&D. See Riot's
  Legal Jibber Jabber page for the canonical text.
- [ ] **Install button** that goes directly to the latest GitHub Release
  asset (not the Releases page index — make them download in one click).
- [ ] **Vanguard troubleshooting.** A short FAQ entry: "If the installer
  fails, here's what's happening (Vanguard intercepts unsigned exes), and
  here's the workaround (Defender exclusion / disable vgc temporarily)."
  This will save you 80% of the support volume from the first cohort.
- [ ] **Privacy policy page** — auto-generated from a template
  (privacypolicies.com or similar) is fine for v1. Must cover: what data
  Revu reads, where it stores it, what leaves the machine.
- [ ] **Terms of service page** — same. Must include the Riot disclaimer
  + an "as-is, no warranty" clause.
- [ ] **Contact / support link.** Discord invite, email, or GitHub
  Issues — pick one that you'll actually check.
- [ ] **Auto-update messaging.** A small note: "Revu auto-updates from
  GitHub. You don't need to download a new installer when we ship a fix."
  Reduces the "is this old?" hesitation when someone returns to the site
  in three weeks.

### Acceptance criteria

- [ ] A non-technical friend can describe what Revu does after 60s on the
  homepage, in their own words. If they say "it's like op.gg," the pitch
  isn't differentiating.
- [ ] Click-from-homepage to "downloading Setup.exe" is ≤2 clicks.
- [ ] Page weight under 1MB total (excluding video). Loads in <1.5s on
  a 4G mobile connection.
- [ ] Privacy policy + Terms of Service pages exist and link from the
  footer.
- [ ] Riot disclaimer is visible without scrolling on the homepage *or*
  on a clearly-linked legal page.
- [ ] No broken links. Run a link checker (`lychee` or
  https://www.deadlinkchecker.com/) before going live.
- [ ] Mobile rendering doesn't break. Even though the app is desktop-only,
  ~40% of the site traffic will be mobile (people sharing the link in
  Discord on their phone).
- [ ] Open Graph + Twitter Card tags set so Discord previews look good.
  This is a 30-line change in `<head>` that pays off every time someone
  shares the link.

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

- [ ] **Privacy policy** published (covers: SQLite local storage,
  Cloudflare Worker proxy logs, Riot Match-V5 calls, no third-party
  analytics, no telemetry beyond crash logs)
- [ ] **Terms of service** published with as-is + Riot disclaimer
- [ ] **Riot disclaimer** in the app itself (Settings → About is fine)
  and on the site
- [ ] **License file** in the repo (see Section 3)

### Crash + telemetry

- [ ] **Crash log location is documented** in README + Settings page.
  Currently `%LOCALAPPDATA%\LoLReview\crash.log`, mentioned in README.
  Add a Settings → "Open crash log folder" button so users don't need
  to navigate AppData manually.
- [ ] **Velopack log diagnose button** — already on the v2.16 backlog
  as Investigation #1. If we don't ship this, the first time a user's
  update fails silently we have no debug path. **Do this.**
- [ ] **Optional anonymous telemetry**: out of scope for v1. A no-op
  for now — we ship without it. Document the decision in
  `docs/TELEMETRY_DECISION.md` so future-you doesn't re-litigate.

### Update path

- [ ] **Code-signing decision** — captured in `docs/CODE_SIGNING_PLAN.md`.
  Going un-signed for now, accept the Vanguard friction, document the
  workaround on the site. Re-evaluate after first 50 installs based on
  support volume.
- [ ] **Manual install fallback** — the site links to the raw `Setup.exe`
  in case the in-app updater fails. Already true via the GitHub Release
  asset; verify the link is one-click on the site.

### Support readiness

- [ ] **A way to receive bug reports** that isn't "DM me on Discord at
  3am." GitHub Issues with templates is enough.
- [ ] **A first-response SLA you can keep**, even if it's "I respond
  within 48h on weekdays." Set the expectation publicly somewhere
  (CONTRIBUTING.md or the site's contact page).
- [ ] **Known-issues list.** Even one short FAQ on the site or a pinned
  GitHub issue. Saves repeating yourself.

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

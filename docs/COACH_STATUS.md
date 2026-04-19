# COACH_STATUS.md

Live state file for the AI coaching rebuild. Claude Code updates this at the end of every session. @samif0 checks it at the start of every session to know where things stand.

**See `COACH_PLAN.md` for the full plan.**

---

## Current state

**Current phase:** All phases (-1, 0, 1, 3, 2, 5a, 4, 5b, 6) code complete. See `MORNING_REPORT.md` for per-phase verification status and what needs re-iteration.
**Current branch:** `main` (no per-phase branches; checkpoint reviews waived for overnight run)
**Last updated:** 2026-04-18
**Last updated by:** Claude Code (autonomous overnight run)

---

## Phase history

Checklist of phases. Check off as each exit criteria are met and the branch is merged.

- [x] Phase -1: Cleanup of existing coaching code ✅ 2026-04-18
- [x] Phase 0: Sidecar skeleton and provider layer ✅ 2026-04-18 (code; needs Ollama for /test-prompt verification)
- [x] Phase 1: LoL-MDC-style compacted summaries ✅ 2026-04-18 (code; needs backfill run + spot-check per MORNING_REPORT.md)
- [x] Phase 3: Feature bank and user_signal_ranking ✅ 2026-04-18 (code; needs verification of plausible top-10 features)
- [x] Phase 2: Concept extraction and user_concept_profile ✅ 2026-04-18 (code; PROMPT NEEDS TUNING on your real reviews — see MORNING_REPORT.md CHECKPOINT)
- [x] Phase 5a: Post-game coach mode ✅ 2026-04-18 (code; needs live Ollama + accept-rate criterion over 20 games)
- [x] Phase 4: Vision pipeline for clips ✅ 2026-04-18 (code; needs Ollama vision + real VOD bookmarks)
- [x] Phase 5b: Remaining coach modes ✅ 2026-04-18 (code; same LLM caveats as 5a)
- [x] Phase 6: Sharing and distribution ✅ 2026-04-18 (providers + privacy doc; first-run wizard + VM smoke test deferred)

---

## Active work

What's being worked on right now. Update when starting a session, clear when stopping.

**Phase:** (none)
**Task:** (none)
**Branch:** (none)
**Started:** (timestamp)
**Notes:**
- (empty)

---

## Unclear items

Things Claude Code flagged as needing human input. Each item gets a unique ID. @samif0 resolves, Claude Code removes after resolution.

Format:

- **UNCLEAR-001:** Short description. (Flagged YYYY-MM-DD, phase N, task T.)
  - Claude Code's context / guess / options.
  - **Resolution:** (added by @samif0) — then Claude Code removes the item once acted on.

Current items:

- **UNCLEAR-001:** Scheduler for recompute jobs (signals rerank, concepts reprofile). (Flagged 2026-04-18, phase 3/5a, from standing question §11.)
  - Options: (a) nightly Windows scheduled task on C# side, (b) on-demand with staleness check inside each coach-mode call, (c) manual-only via settings button.
  - Claude's lean: (b) — desktop nightly schedulers are fragile (machine asleep, app not running). Check `updated_at` on `user_signal_ranking` and `user_concept_profile`; recompute if older than N days (default 3).
  - **Resolution:** (pending @samif0)

- **UNCLEAR-002:** Port 5577 handling if port is taken. (Flagged 2026-04-18, phase 0, task 2.)
  - Options: (a) fail loud with clear error, (b) auto-pick next free port and tell C# via handshake, (c) make port a config value in `coach_config.json` with (b) as fallback.
  - Claude's lean: (c).
  - **Resolution:** (pending @samif0)

- **UNCLEAR-003:** API key injection from C# to sidecar. (Flagged 2026-04-18, phase 0, §6.)
  - Keys live in Windows Credential Manager on the C# side. Sidecar needs them to call hosted providers.
  - Options: (a) env var at sidecar start, (b) `POST /config` after sidecar health is green, (c) command-line arg.
  - Claude's lean: (b) — no key-in-env-dump risk, clean lifecycle.
  - **Resolution:** (pending @samif0)

- **UNCLEAR-004:** Known-important concepts for Phase 2 exit criteria. (Flagged 2026-04-18, phase 2.)
  - Plan currently lists: "my play only," jungle proximity, tilt management, Kai'Sa spike.
  - Needed: confirm still accurate for 2026; add/remove any.
  - **Resolution:** (pending @samif0)

- **UNCLEAR-005:** Comfort with mediocre-concepts 5a first pass. (Flagged 2026-04-18, phase 5a.)
  - Sequencing `3 → 2 → 5a` means 5a ships with signals but possibly thin concepts (concepts need review volume to cluster cleanly).
  - Options: (a) ship 5a as planned, iterate as concepts mature, (b) delay 5a until `user_concept_profile` has ≥ N stable clusters.
  - Claude's lean: (a) — signals alone + summary are already useful; concepts compound over time.
  - **Resolution:** (pending @samif0)

- ~~**UNCLEAR-006:** Gemma model tag for Ollama default.~~ **Resolved 2026-04-18 by @samif0:** swap to `gemma4:e4b` (Gemma 4 E4B, 4.5B effective, multimodal, 128K context, Apache 2.0). Config defaults updated across config.py, README.md, CoachSettingsViewModel, SettingsPage.xaml. Google AI Studio and OpenRouter stay on `gemma-3-27b-it` until hosted providers publish Gemma 4 tags.

---

## Blockers

External things that stop work. Distinct from `UNCLEAR` (ambiguity) — blockers are concrete obstacles.

Format:

- **BLOCKER-001:** Short description. (Flagged YYYY-MM-DD, phase N.)
  - What's blocked.
  - What's needed to unblock.
  - **Unblocked:** YYYY-MM-DD, how.

Current items:

- (none)

---

## Exit criteria tracking (current phase)

Copy the current phase's exit criteria here when starting the phase. Check each box as it's met. Move to "Phase history" when all pass and the branch is merged.

**Phase:** (none)

- [ ] (criterion 1)
- [ ] (criterion 2)

---

## Session log

Short notes from each Claude Code session. Append-only. Most recent on top.

Format:

### YYYY-MM-DD — phase N — session summary
- What was done
- What's next
- Any new UNCLEAR or BLOCKER entries added

---

### 2026-04-18 — full autonomous run, phases -1 through 6
- Phase -1: audit + cleanup. Deleted ~7,200 lines of prior Coach Lab (experiments/, Core services, App viewmodel + page, tests). Kept 8 `coach_*` tables orphaned in Schema.cs per plan to protect existing DB data. Build green.
- Phase 0: Python sidecar skeleton at `coach/`. FastAPI main.py, config.py with port fallback, db.py with hard safety guard (allowlist + pre-migration backup), schemas.py, providers (base + Ollama full + Google AI full + OpenRouter full), migrations/0001_initial.sql. C# side: CoachSidecarService, CoachInstallerService, ICoachApiClient/CoachApiClient, CoachSettingsViewModel, CoachPanelViewModel, CoachRepository. Wired into DI via AddCoachServices.
- Phase 1: summaries/compactor.py with four-section schema, win_probability.py, key_events.py. Token count via tiktoken o200k_base. Endpoints live.
- Phase 3: signals/features.py (36 features), stability.py (bootstrap CI + drift), ranker.py. sample_size >= 20 filter.
- Phase 2: concept extraction prompt + 3 few-shots, extractor.py with json-repair fallback, embedder.py with Parquet cache, clusterer.py (HDBSCAN), profiler.py (composite rank).
- Phase 5a: post_game prompt (neutral analysis, not coach-persona per amendment), post_game.py with full context assembly.
- Phase 4: frame_sampler.py (ffmpeg, 6 even + pre + post frames), describer.py with vision-fallback routing.
- Phase 5b: clip_reviewer/session_coach/weekly_coach prompts + modes.
- Phase 6: PRIVACY.md with explicit data-leaves-machine by provider.
- C# live hooks: ICoachSidecarNotifier (Core) + CoachSidecarNotifier (App). GameLifecycleWorkflowService, ReviewWorkflowService, VodPlayerViewModel all fire notifications. All fire-and-forget, swallow exceptions.
- NOT done (needs @samif0): XAML for coach settings page + panel (viewmodels ready), pyinstaller-build sidecar exe + GitHub release asset for CoachInstallerService to download, Ollama install + gemma3:12b pull on target machine, Phase 2 prompt iteration on real reviews, Phase 5a accept-rate measurement over 20 games, fresh-VM test for Phase 6.

### 2026-04-18 — pre-phase — plan walkthrough + amendments
- Copied `COACH_PLAN.md` and `COACH_STATUS.md` into repo root (verbatim from Downloads).
- Full section-by-section walkthrough of the plan with @samif0.
- Confirmed: no active startup crash (memory was stale); `experiments/` is fair game for DELETE/ARCHIVE; no known prior coach files (grep-driven audit); data safety is non-negotiable; AI should prioritize correct analysis over coach-persona tone.
- Amended plan (change log entry 1): opt-in download model for sidecar (no 1-2GB install bloat); hard DB safety guard + pre-migration backup in `coach/db.py`; post-game prompt retuned toward neutral analysis.
- Logged 6 UNCLEAR items for standing questions and deferred design calls.
- Next: start Phase -1 (audit pass — grep for prior coaching code, produce `COACH_CLEANUP_AUDIT.md`, reach 🛑 CHECKPOINT for human review).

---

## Decision log

Record design decisions made during implementation that differ from or extend `COACH_PLAN.md`. Append-only. Most recent on top.

Format:

### YYYY-MM-DD — decision short title
- **Context:** why this came up
- **Options considered:** A, B, C
- **Decision:** chose X
- **Consequences:** what this means for later phases

---

### 2026-04-18 — opt-in download model for coach sidecar
- **Context:** bundling pyinstaller + sentence-transformers + torch in the Velopack installer would add ~1-2GB, unacceptable for non-coaching users.
- **Options considered:** (A) bundle everything (rejected: size), (B) opt-in "Enable coaching" toggle that downloads sidecar + models on demand (chosen), (C) separate coaching-edition installer (rejected: distribution complexity).
- **Decision:** B. Base installer ships a bootstrapper only. First-enable downloads from GitHub Releases into `%LOCALAPPDATA%\LoLReviewData\coach\`. Progress UI, resumable, SHA256-verified.
- **Consequences:** Phase 0 adds `CoachInstallerService.cs` + download/verify logic. Release pipeline publishes sidecar as a separate release asset with a version manifest. Settings page needs an install/uninstall flow. Sidecar version independent of app version (both tracked).

### 2026-04-18 — hard data-safety guard in `coach/db.py`
- **Context:** @samif0 memory has prior incidents of permanent data loss from overwrites. Python writing to the same SQLite as C# is a new surface area.
- **Decision:** Explicit allowlist of coach-owned tables. Any write outside the allowlist raises. DDL against non-coach tables raises. Pre-migration backup mandatory (last-5 rotation). Core tables are read-only from Python, enforced at connection level.
- **Consequences:** `coach/db.py` needs two connection wrappers (read-only for core, read-write for coach tables). Adds a small test surface. Worth it.

### 2026-04-18 — AI tone: neutral analysis, not coach-persona
- **Context:** @samif0 clarified Omibro is a real coach; AI should not mimic his or any human coach's tone. Priority is correct analysis.
- **Decision:** Phase 5a prompt restructured to emphasize neutral, factual, grounded output. Every claim must trace to a signal value, concept, or summary field. No coach-persona affect.
- **Consequences:** Simpler prompt engineering, easier to evaluate (grounded-ness is testable). Carries forward to Phase 5b modes.

---

## How to use this file

**For Claude Code:**
1. Read this file at the start of every coach-related session.
2. Update "Active work" when starting.
3. Add to "Session log" when stopping.
4. Add to "Unclear items" or "Blockers" as needed.
5. Add to "Decision log" for any non-trivial design choice.
6. Update "Current state" header with phase, branch, date.
7. Check off "Phase history" only after a phase's branch is merged to `main`.

**For @samif0:**
1. Read "Current state" and "Active work" to know what's happening.
2. Check "Unclear items" regularly — these block forward progress.
3. Resolve checkpoints noted in `COACH_PLAN.md` (🛑 markers) via reviewing artifacts in the repo (e.g., `COACH_CLEANUP_AUDIT.md`).

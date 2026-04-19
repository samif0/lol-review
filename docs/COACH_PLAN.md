# COACH_PLAN.md

**Owner:** @samif0
**Repo:** samif0/lol-review
**Status:** Active build plan
**Last updated:** 2026-04-18 (amended)

This document is the single source of truth for building the AI coaching layer in lol-review. Claude Code should read this file at the start of every session that touches coach-related code and follow it phase by phase.

---

## 0. How to use this document

1. **Start every coach-related session** by reading this file in full, then checking `COACH_STATUS.md` for current phase state.
2. **Work one phase at a time.** Each phase has explicit exit criteria. Do not start the next phase until the current phase's exit criteria are met and @samif0 has confirmed.
3. **Human checkpoints are marked 🛑 CHECKPOINT.** Stop and wait for confirmation. Do not proceed past a checkpoint autonomously.
4. **Each phase gets its own branch.** Branch naming: `coach/phase-N-short-name` (e.g., `coach/phase-0-sidecar`). Merge to `main` only after phase exit criteria pass.
5. **Commits should be atomic and descriptive.** Reference the phase and task number in commit messages (e.g., `phase-1: task 3 — implement win probability module`).
6. **If something is unclear, stop and ask.** Do not guess schema, design choices, or scope. Add an `UNCLEAR` note to `COACH_STATUS.md` and wait.

---

## 1. Project context

### What lol-review is

A local-first WinUI 3 desktop app for reviewing League of Legends games. Watches the LCU, captures post-game stats, lets the user write structured reviews, tracks objectives and rules, stores VODs and bookmarks, and persists everything to a local SQLite DB at `%LOCALAPPDATA%\LoLReviewData\lol_review.db`. Updated via Velopack / GitHub Releases.

**See `CODEBASE_ONBOARDING.md` for the full architecture read.** The onboarding doc is authoritative for the existing codebase; this plan is authoritative for the coach rebuild.

### What this plan adds

An AI coaching layer that sits on top of the existing data model. Four coach modes (clip, post-game, session, weekly), backed by three new derivations computed from user data:

- **Compacted game summaries** (LoL-MDC pattern) — structured JSON that makes LLMs competent on match data
- **User concept profile** — emergent coaching vocabulary extracted from the user's own review text
- **User signal ranking** — per-user predictive feature ranking from their own game outcomes

The coaching behavior is not hard-coded. The vocabulary comes from the user's reviews. The attention comes from correlations in the user's games. The coach reasons in text over structured summaries, uses vision only as a structured-description generator for clips.

### Design principles, in priority order

1. **Emergent over hard-coded.** No role-specific assumptions, no meta-specific constants, no fixed coaching frameworks. Structure lives in the data, not in code.
2. **Local-first, shareable.** Default configuration uses local Ollama for @samif0's own use. Architecture supports hosted providers (Google AI Studio, OpenRouter) so shared users don't need a GPU.
3. **Additive to the existing codebase.** Never break existing features. All new tables, services, and UI panels are additive.
4. **The user approves, the coach drafts.** Coach output is always a suggestion shown to the user, never auto-written into the DB.
5. **Phased delivery.** Every phase produces something working on its own. Claude Code can stop cleanly at any phase boundary.

### Non-goals

- Training a bot to play League of Legends
- Fine-tuning a model in Phases -1 through 6 (logging edits for *possible future* fine-tune is a side effect, not a goal)
- Real-time in-game overlay coaching
- Replacing the human coach

---

## 2. Architecture overview

~~~
┌─────────────────────────────────────────────────────────┐
│  LoLReview.App (WinUI 3)                                │
│  - ReviewViewModel, DashboardViewModel, etc.            │
│  - New: CoachPanelViewModel, CoachSettingsViewModel     │
└───────────────────────┬─────────────────────────────────┘
                        │ HTTP (localhost:5577)
                        ▼
┌─────────────────────────────────────────────────────────┐
│  coach/ (Python FastAPI sidecar)                        │
│  - summaries/: LoL-MDC compacted JSON builders          │
│  - concepts/:  concept extraction + clustering          │
│  - signals/:   per-user feature ranking                 │
│  - modes/:     4 coach modes                            │
│  - providers/: LLM provider abstraction                 │
│  - vision/:    clip frame extraction + VLM description  │
└───────────┬─────────────────────────────┬───────────────┘
            │                             │
            ▼                             ▼
    ┌──────────────┐            ┌──────────────────────┐
    │ SQLite       │            │ LLM Provider         │
    │ lol_review.db│            │ (Ollama / Google AI  │
    │  coach reads │            │  Studio / OpenRouter)│
    │  + writes    │            │                      │
    │  coach tables│            │                      │
    └──────────────┘            └──────────────────────┘
~~~

### Key architectural decisions

**D1. Sidecar process, not embedded.** Python FastAPI server on localhost. C# starts/stops it as a child process (like `ffmpeg.exe`), communicates over HTTP. Clean language boundary, independent deploy cadence.

**D2. SQLite is the integration contract.** Coach reads `lol_review.db`. Coach writes to *new* coach-specific tables only. C# reads those tables directly when it needs the data. No RPC for data, only for operations.

**D3. Provider abstraction with three implementations.**
- `OllamaProvider` — local, default for @samif0
- `GoogleAIProvider` — hosted Gemma, for shared distribution (users bring API key)
- `OpenRouterProvider` — model-flexible fallback (users bring API key)

**D4. Emergent structure, not hard-coded coaching.** The feature bank (Phase 3) is the only place structure lives in code, and it's intentionally descriptive (`gold_diff_at_15`), never prescriptive (`adc_cs_target`).

**D5. Vision as description generator, not reasoner.** Gemma vision produces structured frame descriptions. Text reasoning runs on descriptions + game data. Reasoning stays in text land.

---

## 3. Repo layout (final state)

~~~
lol-review/
├── src/                              # existing WinUI + Core (additive changes only)
│   ├── LoLReview.App/
│   │   ├── Services/
│   │   │   └── CoachSidecarService.cs       [NEW - Phase 0]
│   │   └── ViewModels/
│   │       ├── CoachPanelViewModel.cs       [NEW - Phase 5]
│   │       └── CoachSettingsViewModel.cs    [NEW - Phase 0]
│   └── LoLReview.Core/
│       └── Data/
│           └── Repositories/
│               └── CoachRepository.cs       [NEW - Phase 1]
├── coach/                             [NEW - entire directory, Phase 0+]
│   ├── pyproject.toml
│   ├── README.md
│   ├── coach/
│   │   ├── __init__.py
│   │   ├── main.py                   # FastAPI app
│   │   ├── config.py
│   │   ├── db.py
│   │   ├── schemas.py                # pydantic models for API
│   │   ├── providers/
│   │   │   ├── base.py
│   │   │   ├── ollama.py
│   │   │   ├── google_ai.py
│   │   │   └── openrouter.py
│   │   ├── summaries/
│   │   │   ├── compactor.py
│   │   │   ├── win_probability.py
│   │   │   └── key_events.py
│   │   ├── concepts/
│   │   │   ├── extractor.py
│   │   │   ├── clusterer.py
│   │   │   ├── profiler.py
│   │   │   └── embedder.py
│   │   ├── signals/
│   │   │   ├── features.py
│   │   │   ├── ranker.py
│   │   │   └── stability.py
│   │   ├── vision/
│   │   │   ├── frame_sampler.py
│   │   │   └── describer.py
│   │   ├── modes/
│   │   │   ├── clip_reviewer.py
│   │   │   ├── post_game.py
│   │   │   ├── session_coach.py
│   │   │   └── weekly_coach.py
│   │   └── prompts/
│   │       ├── concept_extraction.md
│   │       ├── frame_description.md
│   │       ├── clip_reviewer.md
│   │       ├── post_game.md
│   │       ├── session_coach.md
│   │       └── weekly_coach.md
│   └── tests/
│       ├── fixtures/
│       └── ...
├── experiments/
│   └── _archive/                     [NEW - Phase -1]
│       └── pre-coach-rebuild-YYYY-MM-DD/
├── COACH_PLAN.md                     # this file
├── COACH_STATUS.md                   [NEW - Phase -1] phase state tracking
├── COACH_CLEANUP_AUDIT.md            [NEW - Phase -1] one-time artifact
└── CODEBASE_ONBOARDING.md            # existing, updated in Phase -1
~~~

---

## 4. Data model additions

All new tables are additive entries in `Schema.AllMigrations` (existing repo pattern, see `DatabaseInitializer.cs`). The coach writes them from Python. C# reads when needed via a new `CoachRepository`.

**Migration ordering:** append new migrations; never modify existing ones.

~~~sql
-- Phase 1
CREATE TABLE IF NOT EXISTS game_summary (
    game_id INTEGER PRIMARY KEY REFERENCES games(id),
    compacted_json TEXT NOT NULL,
    win_probability_timeline_json TEXT,
    key_events_json TEXT,
    summary_version INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    token_count INTEGER
);

-- Phase 2
CREATE TABLE IF NOT EXISTS review_concepts (
    id INTEGER PRIMARY KEY,
    game_id INTEGER NOT NULL REFERENCES games(id),
    source_field TEXT NOT NULL,
    concept_raw TEXT NOT NULL,
    concept_canonical TEXT,
    polarity TEXT NOT NULL,
    span TEXT NOT NULL,
    cluster_id INTEGER,
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_review_concepts_game ON review_concepts(game_id);
CREATE INDEX IF NOT EXISTS idx_review_concepts_cluster ON review_concepts(cluster_id);

CREATE TABLE IF NOT EXISTS user_concept_profile (
    concept_canonical TEXT PRIMARY KEY,
    frequency INTEGER NOT NULL,
    recency_weighted_frequency REAL NOT NULL,
    positive_count INTEGER NOT NULL,
    negative_count INTEGER NOT NULL,
    neutral_count INTEGER NOT NULL,
    win_correlation REAL,
    last_seen_at INTEGER NOT NULL,
    rank INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

-- Phase 3
CREATE TABLE IF NOT EXISTS feature_values (
    game_id INTEGER NOT NULL REFERENCES games(id),
    feature_name TEXT NOT NULL,
    value REAL,
    PRIMARY KEY (game_id, feature_name)
);
CREATE INDEX IF NOT EXISTS idx_feature_values_name ON feature_values(feature_name);

CREATE TABLE IF NOT EXISTS user_signal_ranking (
    feature_name TEXT PRIMARY KEY,
    spearman_rho REAL NOT NULL,
    partial_rho_mental_controlled REAL,
    ci_low REAL NOT NULL,
    ci_high REAL NOT NULL,
    sample_size INTEGER NOT NULL,
    stable BOOLEAN NOT NULL,
    drift_flag BOOLEAN NOT NULL,
    user_baseline_win_avg REAL,
    user_baseline_loss_avg REAL,
    rank INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

-- Phase 4
CREATE TABLE IF NOT EXISTS clip_frame_descriptions (
    bookmark_id INTEGER NOT NULL REFERENCES vod_bookmarks(id),
    frame_timestamp_ms INTEGER NOT NULL,
    frame_path TEXT NOT NULL,
    description_text TEXT NOT NULL,
    model_name TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (bookmark_id, frame_timestamp_ms)
);

-- Phase 5
CREATE TABLE IF NOT EXISTS coach_sessions (
    id INTEGER PRIMARY KEY,
    mode TEXT NOT NULL,
    scope_json TEXT NOT NULL,
    context_json TEXT NOT NULL,
    response_text TEXT NOT NULL,
    response_json TEXT,
    model_name TEXT NOT NULL,
    provider TEXT NOT NULL,
    latency_ms INTEGER,
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS coach_response_edits (
    coach_session_id INTEGER PRIMARY KEY REFERENCES coach_sessions(id),
    edited_text TEXT NOT NULL,
    edit_distance INTEGER,
    created_at INTEGER NOT NULL
);
~~~

All coach table names are prefixed or scoped clearly so future audits can identify them: `game_summary`, `review_concepts`, `user_concept_profile`, `feature_values`, `user_signal_ranking`, `clip_frame_descriptions`, `coach_sessions`, `coach_response_edits`.

---

## 5. HTTP API

FastAPI on `localhost:5577`. JSON in, JSON out. All endpoints documented in `coach/README.md` (Phase 0 task).

~~~
GET  /health
GET  /config
POST /config

POST /summaries/build/{game_id}
POST /summaries/build-all?since=...
GET  /summaries/{game_id}

POST /concepts/extract/{game_id}
POST /concepts/extract-all?since=...
POST /concepts/recluster
GET  /concepts/profile

POST /signals/compute-features/{game_id}
POST /signals/compute-features-all?since=...
POST /signals/rerank
GET  /signals/ranking

POST /vision/describe-bookmark/{bookmark_id}

POST /coach/clip-review           { bookmark_id }
POST /coach/post-game             { game_id }
POST /coach/session               { since?, until? }
POST /coach/weekly                { since?, until? }
POST /coach/log-edit              { coach_session_id, edited_text }
~~~

---

## 6. LLM Provider interface

~~~python
# coach/providers/base.py
from abc import ABC, abstractmethod
from pydantic import BaseModel

class LLMMessage(BaseModel):
    role: str            # 'system' | 'user' | 'assistant'
    content: str | list  # list form for multimodal

class LLMRequest(BaseModel):
    messages: list[LLMMessage]
    model: str
    temperature: float = 0.3
    max_tokens: int = 2000
    response_format: str | None = None   # 'json' for structured output
    images: list[bytes] | None = None

class LLMResponse(BaseModel):
    text: str
    model: str
    provider: str
    input_tokens: int | None = None
    output_tokens: int | None = None
    latency_ms: int

class LLMProvider(ABC):
    @abstractmethod
    async def complete(self, req: LLMRequest) -> LLMResponse: ...
    @abstractmethod
    async def embed(self, texts: list[str]) -> list[list[float]]: ...
    @abstractmethod
    def supports_vision(self) -> bool: ...
    @abstractmethod
    def supports_json_mode(self) -> bool: ...
~~~

Config lives at `%LOCALAPPDATA%\LoLReviewData\coach_config.json`:

~~~json
{
  "provider": "ollama",
  "ollama":      { "base_url": "http://localhost:11434", "model": "gemma3:12b", "vision_model": "gemma3:12b" },
  "google_ai":   { "api_key": "...", "model": "gemma-3-27b-it" },
  "openrouter":  { "api_key": "...", "model": "google/gemma-3-27b-it" }
}
~~~

API keys stored in Windows Credential Manager on the C# side, never in the JSON file.

**Note on model naming:** `gemma3` strings reflect what's available in Ollama at the time of writing. When Gemma 4 lands in Ollama or the hosted providers, swap the model string in config — no code changes needed.

---

## 7. Phases

Sequencing:

> **-1 (Cleanup) → 0 (Sidecar) → 1 (Summaries) → 3 (Signals) → 2 (Concepts) → 5a (post-game mode) → 4 (Vision) → 5b (remaining modes) → 6 (Sharing)**

Signals (Phase 3) runs before Concepts (Phase 2) because signal ranking gives the post-game coach its first useful form even before concepts stabilize. Concepts need review volume; signals work from day 1.

Each phase is on its own branch. Merges to `main` happen only after exit criteria and 🛑 CHECKPOINT confirmation.

---

### Phase -1: Cleanup of existing coaching code

**Branch:** `coach/phase-neg1-cleanup`
**Goal:** Remove, archive, or flag prior coaching code so the new architecture starts clean.

**Tasks:**

1. **Audit pass.** Scan the repo systematically for prior coaching code. Output: `COACH_CLEANUP_AUDIT.md` at repo root. Look for:
   - Anything under `experiments/` dealing with LLM calls, coaching prompts, Gemma/Ollama/OpenAI/Anthropic/Google AI references, or review-text generation
   - Files under `src/` matching `*Coach*`, `*AI*`, `*LLM*`, `*Gemma*`, `*Prompt*`
   - References in `ReviewViewModel`, `VodPlayerViewModel`, `AnalyticsViewModel`, `DashboardViewModel` to coach-related services (even stubbed)
   - DI registrations in `App.xaml.cs` for coach-adjacent services
   - Entries in `Schema.AllMigrations` or `DatabaseInitializer.cs` for coach-related tables
   - NuGet packages or Python deps brought in for AI coaching and unused elsewhere
   - Prompts, `.md` files, or text templates under `docs/` or `experiments/` that look like system prompts
   - Config entries in `config.json` handling or `AppDataPaths.cs` for AI-related paths
   - Mockups under `mockups/` referencing AI features

   For each finding, record: file path, line ranges, one-line description, classification.

2. **Classification.** Each finding gets exactly one of:
   - `DELETE` — dead code, throwaway experiments, unwired stubs
   - `ARCHIVE` — historical/reference value, no live dependency
   - `REFACTOR` — function still needed but shape doesn't fit; flag and defer, do NOT refactor now
   - `KEEP` — matches keywords but unrelated to AI coaching (document why)
   - `UNCLEAR` — ambiguous; surface to @samif0

3. 🛑 **CHECKPOINT — @samif0 reviews `COACH_CLEANUP_AUDIT.md`.** Do not proceed to execution until classifications are confirmed. Expect some items to be reclassified.

4. **Execute cleanup.** One logical group per commit. Each commit references the audit.
   - `ARCHIVE` items: move to `experiments/_archive/pre-coach-rebuild-YYYY-MM-DD/`, add a short `README.md` in that directory explaining what each archived item was and why it was retained. Nothing in `_archive/` is imported or referenced by live code.
   - `DELETE` items: remove.
   - `REFACTOR` items: add `// TODO(coach-rebuild): see COACH_CLEANUP_AUDIT.md entry #N` comment at the top of the file pointing to the phase that will handle it. Do not touch the code body.
   - Update `.gitignore` for any coach-adjacent build artifacts (`__pycache__/`, `.venv/`, etc.).

5. **Database cleanup (code only, not data).** If legacy coach-related tables exist in `Schema.AllMigrations`, do NOT drop them from migrations (that would corrupt existing user DBs). Instead, list them in the audit. Phase 0 will decide whether to rename or leave orphaned.

6. **Build verification after each delete commit.** Run the existing MSBuild command (from `CODEBASE_ONBOARDING.md`) and confirm nothing breaks. If the build fails, the last delete is the culprit. Revert it and reclassify to `REFACTOR`.

7. **Document the rebuild.** Add a new section to `CODEBASE_ONBOARDING.md` titled `## AI Coaching Rebuild (YYYY-MM-DD)`:
   - What was cleaned up (summary)
   - Where archived material lives
   - Reference to `COACH_PLAN.md`
   - Current phase status
   - Bump the "Current Local State As Of" date

8. **Create `COACH_STATUS.md` at repo root.** Template is shipped alongside this plan — use it as the initial content.

9. **Open PR, merge after human review.** PR title: `coach: phase -1 — cleanup of prior coaching code`. Link to `COACH_CLEANUP_AUDIT.md` in the PR description. Merge only after build green and @samif0 confirms.

**Exit criteria:**
- `COACH_CLEANUP_AUDIT.md` exists and is confirmed
- No files contain active coaching code outside what's explicitly kept
- Archived material is under `experiments/_archive/pre-coach-rebuild-YYYY-MM-DD/` with context README
- Build green, existing tests pass, app launches, core flows work (champ select prompt, post-game capture, review save, VOD linking, updater)
- `CODEBASE_ONBOARDING.md` and `COACH_STATUS.md` reflect state
- Branch merged to `main`

**Estimated effort:** 1–2 days.

---

### Phase 0: Sidecar skeleton and provider layer

**Branch:** `coach/phase-0-sidecar`
**Goal:** Python process starts under C# control, speaks HTTP, talks to an LLM provider. No LoL-specific logic yet.

**Tasks:**

1. Create `coach/` package with `pyproject.toml`. Dependencies:
   - `fastapi`, `uvicorn[standard]`, `pydantic>=2`
   - `httpx`, `ollama`
   - `sqlite-utils`
   - `numpy`, `scipy`, `pandas`
   - `sentence-transformers`, `hdbscan`
   - `python-Levenshtein`
   - `ffmpeg-python`
   - Dev: `pytest`, `pytest-asyncio`, `ruff`, `mypy`

2. Implement `coach/main.py` — FastAPI app, `/health` endpoint, `/config` GET/POST.

3. Implement `coach/providers/base.py` with the `LLMProvider` ABC (schema above).

4. Implement `OllamaProvider` fully. Stub `GoogleAIProvider` and `OpenRouterProvider` with the interface in place — they raise `NotImplementedError` until Phase 6.

5. Implement `coach/config.py` — loads from `%LOCALAPPDATA%\LoLReviewData\coach_config.json`, exposes a `get_provider()` factory. Writes via `POST /config`.

6. Implement `coach/db.py`:
   - SQLite connection to `lol_review.db`. WAL mode. Read connections separate from write connections.
   - **Hard data-safety guard.** Maintain an explicit allowlist of coach-owned tables (`game_summary`, `review_concepts`, `user_concept_profile`, `feature_values`, `user_signal_ranking`, `clip_frame_descriptions`, `coach_sessions`, `coach_response_edits`). Any write (INSERT/UPDATE/DELETE) to a table outside this allowlist raises and aborts. Any DDL (ALTER/DROP/CREATE) against non-coach tables raises and aborts. Core tables are read-only from Python.
   - **Pre-migration backup.** Before running any migration, copy `lol_review.db` to `lol_review.db.backup-YYYY-MM-DD-HHMMSS` alongside it. Keep last 5 backups, prune older. Never overwrite without a fresh backup.
   - Migration runner for coach tables (additive only, CREATE TABLE IF NOT EXISTS only). Migrations live in `coach/migrations/NNNN_description.sql`. Never modifies core schema.
   - Read-only helpers for core tables the coach needs (`games`, `session_log`, `matchup_notes`, `vod_files`, `vod_bookmarks`, `game_events`, `derived_event_instances`, `objectives`, `rules`).

7. **C# side:** `CoachSidecarService.cs` in `src/LoLReview.App/Services/`.
   - Starts the Python process at app start (after DI is wired, before shell creation).
   - Monitors health via `GET /health` on a polling timer.
   - Stops on app exit.
   - Model after `GameMonitorService` lifecycle patterns.
   - Logs to a new `coach.log` file under `%LOCALAPPDATA%\LoLReview\`.

8. **C# side:** `CoachSettingsViewModel.cs` — a new settings page. Provider dropdown, model field, API key field (stored in Windows Credential Manager), test-connection button.

9. **Packaging — opt-in download model.** Base installer stays small. Coaching is gated behind an **"Enable coaching"** toggle in settings.
   - Ship a lightweight bootstrapper (`CoachInstallerService.cs`) that, on first-enable, downloads:
     - the sidecar `.exe` (pyinstaller-built, hosted in GitHub Releases as a separate artifact)
     - required model files (sentence-transformers embedding model; Ollama models are user-installed separately)
   - Target location: `%LOCALAPPDATA%\LoLReviewData\coach\bin\` and `%LOCALAPPDATA%\LoLReviewData\coach\models\`.
   - Progress UI in settings. Resumable on failure. SHA256 verification.
   - Sidecar is NOT bundled in the Velopack installer. Only the bootstrapper + version manifest.
   - Release pipeline builds sidecar separately and uploads as a release asset.

10. **Debug endpoint.** `POST /coach/test-prompt` takes a string, returns the provider's completion. Used for smoke-testing.

**Exit criteria:**
- Install app from local debug build. Sidecar starts. C# logs show `/health` returning 200.
- Settings page loads, shows current provider config.
- `POST /coach/test-prompt` with body `{"prompt": "Name one League of Legends champion."}` returns a sensible response via Ollama.
- Sidecar exit is clean when the app closes. No orphaned Python processes.
- Release pipeline produces a working installer with the sidecar bundled (manual VM test).

**Estimated effort:** 3–4 days.

---

### Phase 1: LoL-MDC-style compacted summaries

**Branch:** `coach/phase-1-summaries`
**Goal:** Deterministic, non-LLM summary generation per game. ~1,500 tokens, four sections.

**Reference:** Kim, Lee, Park (2025), "Structured Summarization of League of Legends Match Data Optimized for Large Language Model Input," Applied Sciences 15(13):7190. The method and the four-section schema come from this paper.

**Tasks:**

1. **Design the compacted JSON schema** in `coach/summaries/compactor.py`. Four sections:
   - **match_overview:** `match_id`, `patch`, `queue`, `duration`, `champion`, `enemy_lane_champion`, `role`, `result`, `kda`, `cs`, `gold`, `damage_dealt`, `vision_score`
   - **team_and_player_stats:** aggregated team-level; per-player detail for the user's player, summarized for teammates and enemies
   - **timeline_view:** 1-minute buckets of `gold`, `xp`, `cs`. `advantage_gold` and `advantage_xp` per minute (team sum minus enemy sum). `win_probability` per minute.
   - **key_events:** top-N events (default N=5) ranked by momentum impact. Each event: `timestamp`, `type`, `participants`, `win_prob_before`, `win_prob_after`, `momentum_impact`.

2. **Implement `win_probability.py`.** Pythagorean expectation per minute: `P1(t) = S1(t)^α / (S1(t)^α + S2(t)^α)` where `S1`, `S2` are team aggregate metrics (gold, XP). Start with `α=1.5`. Final probability = mean of gold-derived and XP-derived. Expose `alpha` as a tunable config with a reasonable default.

3. **Implement `key_events.py`.** Algorithm from the paper:
   - For each event in `derived_event_instances` (preferred) or `game_events` (fallback), compute `momentum_impact = |P(t+1) - P(t)|`.
   - Sort descending, keep top N.

4. **Source selection.** Prefer `derived_event_instances` for cleaner event windows. Fall back to raw `game_events` if derived are missing. Annotate the source in the output JSON.

5. **Write result to `game_summary`** with `summary_version=1`. Version bumps on schema changes so stale summaries can be detected and recomputed.

6. **Token count measurement.** Use `tiktoken` with `o200k_base` and `cl100k_base` encodings. Store `token_count` in the row (use `o200k_base` as canonical). Target: 1,200–2,500 tokens per summary.

7. **HTTP endpoints** (per Section 5):
   - `POST /summaries/build/{game_id}`
   - `POST /summaries/build-all?since=...`
   - `GET /summaries/{game_id}`

8. **Live-ingest hook.** Add a fire-and-forget HTTP call from `GameService.ProcessGameEndAsync` to `POST /summaries/build/{game_id}` after the game is persisted. If the sidecar is unreachable, log and move on — summaries can be backfilled.

9. **CLI.** `python -m coach.summaries build --all` for backfill. `python -m coach.summaries build --game-id N` for single game.

10. **C# side:** `CoachRepository.cs` in `src/LoLReview.Core/Data/Repositories/` with read methods for `game_summary`. Wire nothing into UI yet (Phase 5 does that).

**Exit criteria:**
- Backfill completes on @samif0's full game history.
- Spot-check 5 recent games: summaries have sane stats, readable timelines, recognizable key events.
- Average token count is 1,200–2,500.
- Live ingest works: play a new game, summary appears in `game_summary` within 30 seconds of the game ending.

**Estimated effort:** 4–5 days.

---

### Phase 3: Feature bank and user_signal_ranking

**Branch:** `coach/phase-3-signals`
**Note:** Phase 3 runs before Phase 2 — see sequencing note in Section 7.

**Goal:** Per-user predictive feature ranking derived from game data. Nothing role-specific is hard-coded.

**Tasks:**

1. **Feature bank** in `coach/signals/features.py`. Features are defined as data. Starting set:
   - **Aggregate:** `kda_ratio`, `kill_participation`, `cs_per_min`, `gold_per_min`, `damage_share`, `vision_score`, `wards_placed`, `wards_killed`, `first_item_time`, `deaths`, `kills_before_10`, `deaths_before_10`, `assists`
   - **Timeline-derived:** `gold_diff_at_10`, `gold_diff_at_15`, `gold_diff_at_20`, `xp_diff_at_10`, `xp_diff_at_15`, `xp_diff_at_20`, `cs_diff_at_10`, `cs_diff_at_15`, `max_gold_deficit_recovered`, `gold_diff_auc`
   - **Event-derived:** `first_blood_participation`, `dragons_participated`, `heralds_participated`, `barons_participated`, `towers_taken_participated`
   - **Matchup-contextual:** `gold_diff_vs_lane_at_15`, `solo_kills_in_lane`, `deaths_to_lane_opponent`
   - **Session-contextual:** `game_number_in_session`, `minutes_since_last_game`, `mental_rating_going_in`, `mental_rating_going_out`

   Feature names are descriptive only. None encode role or meta assumptions.

2. **Compute and persist.** `POST /signals/compute-features/{game_id}` computes all features for that game, writes to `feature_values`. `POST /signals/compute-features-all` backfills.

3. **`coach/signals/ranker.py`.** For each feature:
   - Spearman ρ with binary win outcome across the user's games
   - Partial Spearman controlling for `mental_rating_going_in`: rank-regress mental against wins, take residuals, correlate feature with residuals (scipy primitives are fine)
   - Require `sample_size ≥ 20` to rank. Below that, feature is excluded from ranking output.

4. **`coach/signals/stability.py`.**
   - **Bootstrap CI:** 1000 resamples, compute ρ each. Use 2.5/97.5 percentiles. If CI crosses 0, set `stable=False`.
   - **Drift detection:** compute ρ on first half vs second half of games. If `|ρ_first − ρ_second| > 0.2`, set `drift_flag=True`.

5. **User baselines.** For each feature, compute mean value on wins and on losses. Store as `user_baseline_win_avg`, `user_baseline_loss_avg`.

6. **Ranking.** Order features by `|partial_rho_mental_controlled|` descending, filtered to `stable=True`. Write to `user_signal_ranking` with rank.

7. **Endpoints:** `POST /signals/rerank`, `GET /signals/ranking`.

8. **CLI:** `python -m coach.signals compute-all`, `python -m coach.signals rerank`.

9. **Nightly job hook (optional in Phase 3, required by Phase 5).** If a scheduler is easy to add on the C# side, call `POST /signals/rerank` nightly. Otherwise, call it on-demand from the coach modes.

**Exit criteria:**
- Top 10 ranked features for @samif0 are plausible (gold-diff and mental-related features should dominate based on prior self-reported patterns).
- Bootstrap CIs are populated. At least some features marked `stable=False` if the sample is small or noisy.
- Drift flags fire on at least one feature (expected given patch changes in the user's history).
- Anything surprising at the top gets investigated for feature-leakage bugs.

**Estimated effort:** 4–5 days.

---

### Phase 2: Concept extraction and user_concept_profile

**Branch:** `coach/phase-2-concepts`
**Goal:** Emergent coaching vocabulary from the user's own review text.

**Tasks:**

1. **Prompt** `prompts/concept_extraction.md`. Directs the LLM to extract 1–5 concepts per review field. Output JSON: `[{concept, polarity, span}]` where `polarity ∈ {positive, negative, neutral}` and `span` is the original fragment. Include 3 few-shot examples drawn from @samif0's real reviews (fetched with permission).

   🛑 **CHECKPOINT after drafting the prompt** — @samif0 reviews output on 20–30 of his own reviews. Iterate until output is clean.

2. **`coach/concepts/extractor.py`.** For each game's review fields (`mistakes`, `went_well`, `focus_next` from `games`; `improvement_note` from `session_log`; `note` from `matchup_notes` joined by game), call the provider. Parse JSON with retry + `json_repair` fallback. Write to `review_concepts`.

3. **`coach/concepts/embedder.py`.** Wrap `sentence-transformers/all-MiniLM-L6-v2`. Cache embeddings in a Parquet file under `%LOCALAPPDATA%\LoLReviewData\coach\embeddings\`.

4. **`coach/concepts/clusterer.py`.** HDBSCAN: `min_cluster_size=2`, cosine distance, `cluster_selection_epsilon=0.15` (tune against data). Assign `cluster_id` to each row in `review_concepts`. For each cluster, pick the canonical label as the shortest member that appears ≥2 times. Singletons get `cluster_id=NULL` and are excluded from the profile unless they recur later.

5. **`coach/concepts/profiler.py`.** For each canonical concept:
   - `frequency`
   - `recency_weighted_frequency` — exponential decay with half-life = 30 days, anchored to `created_at`
   - `positive_count`, `negative_count`, `neutral_count`
   - `win_correlation` — Spearman between "concept present in mistakes/focus_next" and loss; between "concept present in went_well" and win (report max-magnitude)
   - Composite rank score: `0.4 * recency_weighted + 0.3 * |win_correlation| + 0.3 * polarity_signal` where `polarity_signal = (negative_count - positive_count) / frequency`
   - Write to `user_concept_profile` with `rank`.

6. **Endpoints:** per Section 5.

7. **Live hook.** `ReviewViewModel.SaveAsync` fires a `POST /concepts/extract/{game_id}` after save. Re-cluster and re-profile nightly via `POST /concepts/recluster`.

8. **CLI:** `python -m coach.concepts extract-all`, `recluster`, `reprofile`.

**Exit criteria:**
- Top 20 concepts in `user_concept_profile` are recognizably @samif0's coaching vocabulary.
- Known-important concepts (from memory: "my play only," jungle proximity, tilt management, Kai'Sa spike, etc.) rank high.
- Noisy one-offs are filtered by clustering.
- Concepts from singleton clusters don't appear in the profile.

**Estimated effort:** 5–7 days. Prompt tuning dominates.

---

### Phase 5a: Post-game coach mode

**Branch:** `coach/phase-5a-postgame`
**Goal:** The first useful coach surface. Runs after a game, drafts review fields, user accepts or edits.

**Tasks:**

1. **`coach/modes/post_game.py`.** Context assembly:
   - `game_summary` for this game
   - `session_log` row for this game
   - Top 15 concepts from `user_concept_profile`
   - Top 10 signals from `user_signal_ranking` with this game's feature values annotated as above/below `user_baseline_win_avg`
   - Recent matchup note for this champion × enemy pair (if exists)
   - Previous game's `focus_next` text (continuity)

2. **`prompts/post_game.md`.** Structure:
   - Role statement: analysis tool. Does NOT mimic a human coach's tone or role. Prioritizes correct, grounded analysis over coaching personality.
   - Tone guidance: neutral, factual, direct. Ground every claim in a signal value, concept, or summary field. No coach-persona affect.
   - User's top concepts (injected).
   - User's top signals with values (injected).
   - Output format: JSON with `mistakes`, `went_well`, `focus_next` fields.

3. **Endpoint** `POST /coach/post-game` returns `{ coach_session_id, response_json }`. Writes to `coach_sessions`.

4. **C# side:** `CoachPanelViewModel.cs` for the review page. "Draft with coach" button. Renders the three fields as editable text boxes pre-filled with the draft. User can edit freely. "Accept" writes to `games` via the existing repo path. "Discard" just closes.

5. **Edit logging.** When the user saves an edited version, fire `POST /coach/log-edit` with `coach_session_id` and the user's final text. Python computes Levenshtein distance against the original draft and writes to `coach_response_edits`.

6. **Integration with existing review flow.** The coach draft is a suggestion pre-filling the existing review fields. The save path is unchanged (`GameRepository.UpdateReviewAsync`). Coach never writes directly to `games`.

**Exit criteria:**
- Post-game coach draft appears within 15 seconds after game ends, for @samif0's next 5 games.
- Drafts are useful enough to read, not discard outright. Accept rate (edited or unedited) > 50% across first 20 games.
- Edit logging works: edits appear in `coach_response_edits` with non-null `edit_distance`.

**Estimated effort:** 5–6 days. Prompt iteration dominates.

---

### Phase 4: Vision pipeline for clips

**Branch:** `coach/phase-4-vision`
**Goal:** Clip frame → structured text description for downstream coach modes.

**Tasks:**

1. **`coach/vision/frame_sampler.py`.** Given a `vod_bookmarks` row (VOD path + timestamp window), use `ffmpeg-python` to extract:
   - 6 frames evenly spaced across the window
   - 1 frame at `start - 2s` (pre-context)
   - 1 frame at `end + 2s` (post-context)
   - Save PNG to `%LOCALAPPDATA%\LoLReviewData\coach_frames\{bookmark_id}\`. Filenames encode the timestamp in ms.
   - If the VOD file is missing or unreadable, fail gracefully with a structured error.

2. **`coach/vision/describer.py`.** For each extracted frame, call the vision provider with `prompts/frame_description.md`. Prompt directs the model to describe concrete observables only — positions, HP bars, visible gold, cooldowns, wards, minion wave state — and explicitly forbids inferring reasoning or predicting future events. Output JSON with keys `positions`, `resources`, `wave_state`, `visible_cooldowns`, `observations`.

3. Store results in `clip_frame_descriptions`.

4. **Endpoint** `POST /vision/describe-bookmark/{bookmark_id}`.

5. **C# hook.** `VodPlayerViewModel` — on bookmark create, fire async `POST /vision/describe-bookmark/{bookmark_id}`. Show "analyzing clip..." indicator in the UI until descriptions land.

6. **Provider-aware routing.** If the configured provider doesn't support vision (`supports_vision() == False`), fall back to Ollama's local vision model regardless of text-provider setting. Expose this as a config option: `vision_override_provider`.

**Exit criteria:**
- Create 5 bookmarks from recent VODs. Each produces frame descriptions within 30 seconds.
- Descriptions are factual. Spot-check: no hallucinated reasoning ("enemy is about to gank" — bad; "enemy jungler visible at top river" — good).
- Missing-frame handling works: corrupted VOD produces a clean error, not a crash.

**Estimated effort:** 3–4 days.

---

### Phase 5b: Remaining coach modes (clip, session, weekly)

**Branch:** `coach/phase-5b-modes`
**Goal:** The three remaining modes, using the same pattern as post-game.

**Tasks:**

1. **`coach/modes/clip_reviewer.py`.** Input `bookmark_id`. Context:
   - Parent game's `game_summary` narrowed to the minute window around the bookmark
   - `clip_frame_descriptions` for this bookmark
   - Matchup note for this champion × enemy pair
   - Relevant concepts from `user_concept_profile` (top 10 filtered by semantic similarity to the frame descriptions — use the embedder)

   Prompt `prompts/clip_reviewer.md`: short output, 2–3 bullet insight on the decision visible in the clip. Ground every insight in an observable from the frame descriptions.

2. **`coach/modes/session_coach.py`.** Input `since` / `until` (defaults: last session, defined as games within last 6 hours with no gap > 60 min). Context:
   - All `game_summary` rows in window
   - All `session_log` rows in window
   - Top 15 concepts
   - Top 10 signals with per-game values

   Prompt `prompts/session_coach.md`: pattern call-out across games in session + one concrete focus for next session. Must ground patterns in specific games from the input.

3. **`coach/modes/weekly_coach.py`.** Input `since` / `until` (default: last 7 days). Context:
   - Aggregate stats over week
   - `objectives` progress
   - Rules adherence streak (read `adherence_streak` from existing C# logic — expose a read endpoint on C# side that the coach queries, OR replicate the query in Python — pick one in implementation)
   - Full `user_concept_profile`
   - Full `user_signal_ranking`

   Prompt `prompts/weekly_coach.md`: GROW-framed output — Goal (what the user was working on), Reality (what happened, grounded in signals and concepts), Options (paths forward), Way Forward (proposed adjustments to `objectives` or `rules`). Weekly coach proposes changes; does not apply them.

4. **Endpoints:** `POST /coach/clip-review`, `POST /coach/session`, `POST /coach/weekly`.

5. **C# side.** Extend `CoachPanelViewModel`:
   - Clip-reviewer UI in the VOD player
   - Session coach entry point in the dashboard or session-log view
   - Weekly coach entry point in the dashboard with date-range picker
   - All modes: user sees output, can edit, edits are logged via `POST /coach/log-edit`

6. **Coach response rendering.** All modes render output as markdown in the UI. No auto-write to any existing table.

**Exit criteria:**
- All four modes produce coherent output on @samif0's data.
- Clip mode grounds insights in visible frame observables.
- Session mode surfaces at least one pattern the user recognizes as real (cross-referenced with memory of the session).
- Weekly mode proposes concrete `objectives` / `rules` adjustments; @samif0 judges whether they're useful.

**Estimated effort:** 7–9 days.

---

### Phase 6: Sharing and distribution

**Branch:** `coach/phase-6-sharing`
**Goal:** Other ADCs can install and use the coach without a GPU or any infrastructure @samif0 runs.

**Tasks:**

1. **First-run wizard** in the installer flow. Asks:
   - "Local (Ollama) or Hosted (Google AI Studio / OpenRouter)?"
   - For Local: link to Ollama install docs, test-connection button, default model `gemma3:12b` (update as Gemma 4 availability confirms).
   - For Hosted: link to Google AI Studio docs for getting a free API key, field to paste, test button. Same for OpenRouter.

2. **Implement `GoogleAIProvider` fully.** Streaming support, rate-limit handling with exponential backoff, retry logic on 5xx.

3. **Implement `OpenRouterProvider` fully.** Same treatment. Model-flexible (users can pick Claude, GPT, Gemma, etc.).

4. **Cost visibility.** Token usage shown per coach call in the UI. Running token total per day in settings. No surprise bills.

5. **Privacy docs.** Add `coach/PRIVACY.md`:
   - Local provider: nothing leaves the machine
   - Hosted provider: compacted summaries and frame descriptions may leave. Raw VOD bytes never leave. Frame descriptions can be generated locally via Ollama even if the text provider is hosted (`vision_override_provider` config).
   - API keys stored in Windows Credential Manager, never logged, never in crash dumps
   - Opt-in telemetry: aggregate counters only, no content

6. **Telemetry, clearly opt-in.** Per-coach-call latency and token counts, aggregated. No game content, no coach responses, no review text. Setting defaults to off.

7. **Shared-user documentation.** Update `coach/README.md` with provider setup, model guidance, privacy summary, troubleshooting.

8. **VM smoke test.** Fresh Windows VM, no Ollama installed, fresh install of lol-review, paste Google AI Studio key, coach works end-to-end within 5 minutes of first launch. Document the test in `COACH_STATUS.md`.

**Exit criteria:**
- Fresh VM test passes.
- All three providers pass their test-connection button.
- Privacy doc published.
- Cost visibility shows accurate token counts for at least one full coach-mode call per provider.

**Estimated effort:** 4–5 days.

---

## 8. Evaluation harness

Parallel to Phase 5a+, not blocking. Lives in `coach/tests/eval/`.

Three-layer eval:

1. **Rubric (LLM-as-judge).** A rubric with 4 dimensions (mistake remediation, scaffolding, actionability, coherence/tone), each 1–3. An LLM scores coach outputs against the rubric. Run on held-out games. Track score trends over prompt iterations.
2. **Task-grounded agreement.** For ~20 held-out games with actual user reviews, compute concept-level Jaccard overlap between coach's proposed `mistakes` and the user's actual ones. Uses Phase 2 clusters for normalization.
3. **Behavioral outcome.** Longitudinal, non-blocking. Track mental-rating distribution, adherence streak, objective completion, LP delta.

Eval scripts are `pytest`-runnable and produce a report committed to `coach/tests/eval/reports/YYYY-MM-DD.md`.

---

## 9. Risks and mitigations

- **Riot payload changes.** Version the summary schema. Treat missing fields as nulls. Log warnings. Same pattern `StatsExtractor` already follows.
- **Malformed JSON from LLM.** `json_repair` fallback. On repeated failure, log and skip. Never crash the coach pipeline.
- **Noisy early clusters.** `min_cluster_size=2`, singletons excluded. Optional manual merge UI in Phase 6+.
- **Feature leakage.** Annotate features as `prospective` vs `retrospective` in a docstring. Trust prospective more.
- **Vision fails on teamfight frames.** Sample frames at known event timestamps from `derived_event_instances`, not just even spacing.
- **Sidecar crashes.** Health monitor with restart logic on the C# side. Status visible in settings.
- **Prompt drift.** Prompts in `.md` files under version control. Each prompt has a header comment with "last tuned against model X on date Y." Eval harness catches regressions.

---

## 10. Definition of done (overall)

The coach is "done" for @samif0's personal use when:

- All 4 coach modes produce useful output on live games
- `user_concept_profile` reflects his coaching vocabulary
- `user_signal_ranking` reflects his actual win predictors
- Edit logging has captured at least 50 coach-draft edits for future fine-tune consideration
- Evaluation rubric scores stay at ≥ 2/3 average across modes for 4 consecutive weeks

The coach is "done" for shareable distribution when:

- Phase 6 exit criteria pass
- At least 3 external users have onboarded via the hosted wizard and used the coach for 2+ weeks without critical bugs
- Privacy doc reviewed

Neither of these triggers a "finish the project" state — the coach is a live system that iterates as the user iterates.

---

## 11. Standing questions for @samif0

Stored here so Claude Code can refer back. Claude Code should add new `UNCLEAR` items to `COACH_STATUS.md`, not this file.

- Target Gemma model tag (local Ollama default): confirm `gemma3:12b` or swap when Gemma 4 lands
- Sidecar bundling method preference: `pyinstaller` (default) vs alternatives
- Whether to add nightly scheduler on C# side for recompute jobs, or trigger on demand from coach-mode calls

---

## 12. Change log for this plan

| Date | Author | Change |
|---|---|---|
| 2026-04-18 | @samif0 + Claude | Amendment 1: opt-in download model for sidecar (§7 Phase 0 task 9); hard DB safety guard + pre-migration backup in `coach/db.py` (§7 Phase 0 task 6); post-game prompt retuned away from coach-persona mimicry toward neutral analysis (§7 Phase 5a task 2) |
| 2026-04-18 | @samif0 (drafted with Claude) | Initial plan |

Append new entries at the top when this file is updated.

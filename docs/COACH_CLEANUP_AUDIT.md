# COACH_CLEANUP_AUDIT.md

**Phase:** -1 (Cleanup of existing coaching code)
**Date:** 2026-04-18
**Author:** Claude Code (autonomous session)
**Branch:** `main` (per @samif0 instruction; not using per-phase branches)
**Status:** Classifications finalized autonomously per @samif0 instruction to push through without checkpoint review.

## Scope

Find and classify every piece of prior AI-coaching code in the repo so Phase 0+ can start from a clean architectural base. Per COACH_PLAN.md §7 Phase -1 task 1.

## Summary of findings

**Total lines of prior coach code:** ~7,200 across 16 src/ files, ~90 KB of Python/PowerShell under `experiments/coach_lab/`, and an entire `.venv-coach-gemma` virtualenv at repo root.

**Key discovery:** The prior system ("Coach Lab") was a Gemma-only, clip-first coaching experiment behind the hidden feature flag `LOLREVIEW_ENABLE_COACH_LAB`. It had its own DB schema (8 coach tables, 3 migration blocks), its own Python worker stack, persistent sidecar client, training service, and a dedicated WinUI page with a 1,068-line viewmodel.

**DB data safety:** Per plan §7 Phase -1 task 5, the 8 `coach_*` tables are **NOT** being dropped from `Schema.AllMigrations`. The CREATE TABLE IF NOT EXISTS statements and ALTER TABLE migrations are kept in place so existing user DBs are not corrupted. @samif0's live DB has 62 coach_moments, 39 gold labels — this data stays untouched.

**Cascade-delete tests depend on coach tables:** `VodRepository.DeleteBookmarkAsync` and `DeleteGameAsync` currently cascade-delete from `coach_moments`/`coach_labels`/`coach_inferences`. Since the coach tables are being orphaned (not dropped), the safe path is to **remove the cascade code from VodRepository** (it'll become dead cleanup for a dead schema) and **delete the tests that validate that cascade**. Orphaned coach rows in the user's DB are harmless — the data is just untouched.

---

## Findings by category

### 1. experiments/ directory

| Path | Lines | Description | Classification |
|---|---|---|---|
| `experiments/coach_lab/README.md` | 113 | Coach Lab bootstrap docs — Gemma stack, hidden page flag, workflow | **DELETE** (replaced by COACH_PLAN.md) |
| `experiments/coach_lab/OBJECTIVE_SUGGESTER_PLAN.md` | 333 | Proposed bundle-planner spec, dated 2026-04-08 | **DELETE** (obsolete; new plan supersedes) |
| `experiments/coach_lab/gemma_stack.py` | 17,987 bytes | Shared Gemma dataset/registration/inference helpers | **DELETE** |
| `experiments/coach_lab/gemma_worker.py` | 14,698 bytes | Persistent worker used by app for drafts/problems/objectives | **DELETE** |
| `experiments/coach_lab/register_gemma_e4b.py` | 1,856 bytes | Registers Gemma-4-E4B-it in `coach_models` | **DELETE** |
| `experiments/coach_lab/train_gemma_e4b.py` | 13,292 bytes | QLoRA pilot for clip-card task | **DELETE** |
| `experiments/coach_lab/setup_gemma_stack.ps1` | 3,889 bytes | Bootstraps `.venv-coach-gemma` | **DELETE** |
| `experiments/coach_lab/export_dataset.py` | 10,554 bytes | Exports clip-card JSONL for training | **DELETE** |
| `experiments/coach_lab/__pycache__/` | — | Build artifact | **DELETE** |
| `experiments/sam3_vod/` | entire dir | Sam 3 VOD analysis experiment (unrelated to coaching); contains `.venv`, `analyze_vods.py`, `patch_windows_fallbacks.py`, `README.md`, `run_local.ps1`, `setup_local_env.ps1`, `profiles/` | **ARCHIVE** (pre-existing, unrelated to coaching, has independent value as a VOD analysis utility) |

### 2. src/LoLReview.App/

| Path | Lines | Description | Classification |
|---|---|---|---|
| `ViewModels/CoachLabViewModel.cs` | 1,068 | Coach Lab page viewmodel — sync/draft/problems/train/suggest commands | **DELETE** |
| `Views/CoachLabPage.xaml` | 640 | Coach Lab page UI | **DELETE** |
| `Views/CoachLabPage.xaml.cs` | 23 | Coach Lab page code-behind | **DELETE** |
| `Services/NavigationService.cs` line 34 | — | `["coachlab"] = typeof(CoachLabPage)` nav entry | **REFACTOR** (remove the one line) |
| `Views/ShellPage.xaml` lines 168-178 | — | `NavCoachLab` sidebar button (collapsed by default, shown when feature flag on) | **REFACTOR** (remove button + FontIcon) |
| `Views/ShellPage.xaml.cs` | — | 5 references to `RefreshCoachLabVisibility`, `CoachLabFeature.IsEnabled()`, `CoachLabFeature.UpdateRuntimeIdentity` (lines 50, 115, 234, 237-246, 274-275) | **REFACTOR** (remove all coach lab visibility logic) |
| `Composition/ServiceCollectionExtensions.cs` lines 57-60, 124 | — | DI registrations for `ICoachSidecarClient`, `ICoachRecommendationService`, `ICoachTrainingService`, `ICoachLabService`, `CoachLabViewModel` | **REFACTOR** (remove 5 DI lines) |
| `ViewModels/VodPlayerViewModel.cs` | — | Coach Lab clip sync (debounce, gate, cancellation): field declarations, constructor injection, `ScheduleCoachLabSync`, `RunCoachLabSyncDebouncedAsync` (lines 22, 31-39, 146-157, 433, 532, 640, 727-770) | **REFACTOR** (remove all coach lab wiring; keep the rest of the viewmodel) |

### 3. src/LoLReview.Core/

| Path | Lines | Description | Classification |
|---|---|---|---|
| `Models/CoachLabModels.cs` | 631 | Coach-specific models (CoachMoment, CoachLabel, CoachInference, CoachRecommendation, CoachModel, CoachDatasetVersion, CoachLabFeature, …) | **DELETE** |
| `Services/CoachLabService.cs` | 2,294 | Main Coach Lab orchestration (sync, moments, labels, inferences, training, recommendations) | **DELETE** |
| `Services/ICoachLabService.cs` | 30 | Interface | **DELETE** |
| `Services/CoachPythonRuntime.cs` | 96 | Python process runtime helpers | **DELETE** |
| `Services/CoachRecommendationService.cs` | 550 | Heuristic + Gemma-based objective recommendation | **DELETE** |
| `Services/ICoachRecommendationService.cs` | 12 | Interface | **DELETE** |
| `Services/CoachSidecarClient.cs` | 552 | HTTP client to Gemma worker | **DELETE** |
| `Services/ICoachSidecarClient.cs` | 12 | Interface | **DELETE** |
| `Services/CoachTrainingService.cs` | 495 | Training pipeline orchestration (register base, export, prepare, fine-tune) | **DELETE** |
| `Services/ICoachTrainingService.cs` | 12 | Interface | **DELETE** |
| `Data/Repositories/VodRepository.cs` | — | `DeleteCoachRowsForBookmarkAsync` / `DeleteCoachRowsForGameAsync` / `DeleteCoachLabelsAsync` / `DeleteCoachInferencesAsync` + call sites at lines 214, 251 | **REFACTOR** (remove all coach cleanup methods and their call sites; coach tables become orphaned but untouched) |
| `Models/PlayerProfile.cs` line 8 | — | Doc-comment mention: "for a future AI coaching model" | **KEEP** (docstring only; PlayerProfile powers AnalyticsViewModel and AnalysisService, fully unrelated to Coach Lab) |
| `Data/AppDataPaths.cs` line 26 | — | `CoachAnalysisDirectory` property | **REFACTOR** (remove — only used by Coach Lab services) |
| `Data/Schema.cs` lines 358-502, 570-591, 634-661 | — | 8 coach table CREATE statements + 3 MigrateCoach* arrays + registrations in AllSchemaStatements and AllMigrations | **REFACTOR — KEEP IN SCHEMA** per plan §7 Phase -1 task 5. Do not drop. Add a comment noting these are legacy tables orphaned by the coach rebuild. Phase 0 will decide whether to repurpose. |
| `Data/DatabaseInitializer.cs` lines 66, 417-485 | — | `RequeueLegacyCoachManualLabelsAsync` (normalizes legacy hybrid coach_labels data) | **REFACTOR** (remove the call + method; data is left as-is, no-op is fine) |

### 4. Tests

| Path | Lines | Description | Classification |
|---|---|---|---|
| `LoLReview.Core.Tests/CoachLabServiceTests.cs` | 777 | CoachLabService unit tests | **DELETE** |
| `LoLReview.Core.Tests/CoachTrainingStatusTests.cs` | 34 | Training status tests | **DELETE** |
| `LoLReview.Core.Tests/DatabaseInitializerTests.cs` lines 150-247 | — | Two tests: `InitializeAsync_RequeuesLegacyCoachLabelsThatStoredInferredReasonData`, `InitializeAsync_DoesNotSeedLegacyAssistCoachModelRows` | **REFACTOR** (remove both tests; the RequeueLegacyCoachManualLabels code being removed makes them moot) |
| `LoLReview.Core.Tests/TypedRepositoryContractTests.cs` lines 183-279 | — | `VodRepository_DeleteBookmarkAsync_RemovesClipBackedCoachRows` — validates cascade-delete into coach tables | **REFACTOR** (remove this test; cascade behavior is being removed) |

### 5. Root-level artifacts

| Path | Size | Description | Classification |
|---|---|---|---|
| `.venv-coach-gemma/` | ~several GB | Python virtualenv for Coach Lab Gemma stack | **DELETE** |
| `.pytest_cache/` | — | Build artifact from prior Python test runs | **DELETE** |
| `.tmp-build/` | — | Build artifact | **DELETE** |
| `build.binlog` | 589 KB | MSBuild binary log (untracked) | **DELETE** (add to .gitignore) |

### 6. .gitignore additions needed

Current `.gitignore` does not cover coach-adjacent Python artifacts. Add:

```
# Coach rebuild (Phase 0+)
coach/__pycache__/
coach/**/__pycache__/
coach/.venv/
coach/**/.venv/
.venv-coach-*/
*.egg-info/
.ruff_cache/
.mypy_cache/
build.binlog
```

### 7. DB tables to orphan (not drop)

Per plan §7 Phase -1 task 5 — kept in `Schema.AllMigrations` with a comment added:

- `coach_players`
- `coach_objective_blocks`
- `coach_moments`
- `coach_labels` (+ `MigrateCoachLabelsAttachment`)
- `coach_inferences` (+ `MigrateCoachInferencesAttachment`)
- `coach_recommendations` (+ `MigrateCoachRecommendationsFeedback`)
- `coach_models`
- `coach_dataset_versions`

These coexist with the NEW coach tables the rebuild introduces (`game_summary`, `review_concepts`, `user_concept_profile`, `feature_values`, `user_signal_ranking`, `clip_frame_descriptions`, `coach_sessions`, `coach_response_edits`). Table names do not collide.

### 8. UNCLEAR items (none)

Per @samif0's instruction to push through without checkpoint review, all findings are classified autonomously. If any classification turns out to be wrong, it can be reverted from git.

## Execution order

Commits will be grouped logically and atomic:

1. **Commit: delete experiments/coach_lab/** — removes Python stack, no C# impact
2. **Commit: delete Core coach services + interfaces + models** — removes 4,681 lines, breaks DI + viewmodel + VodPlayer
3. **Commit: delete App coach viewmodel + page + nav entry + DI registration** — restores build
4. **Commit: refactor VodPlayerViewModel to remove coach sync** — restores build
5. **Commit: refactor ShellPage (remove NavCoachLab + visibility logic)** — restores build
6. **Commit: refactor VodRepository (remove coach cascade)** — restores tests
7. **Commit: refactor DatabaseInitializer (remove RequeueLegacyCoachManualLabels)** — restores tests
8. **Commit: remove coach tests (CoachLabServiceTests, CoachTrainingStatusTests, cascade test, requeue tests)** — tests pass
9. **Commit: remove CoachAnalysisDirectory from AppDataPaths** — trivial
10. **Commit: add comment to Schema.cs documenting orphaned coach_* tables** — documentation
11. **Commit: delete .venv-coach-gemma, .pytest_cache, build.binlog, update .gitignore**
12. **Commit: update CODEBASE_ONBOARDING.md with rebuild section**

Build verified after each group per plan §7 Phase -1 task 6. If build fails, that group's deletion is reverted and reclassified to REFACTOR.

## Out of scope for this audit

- The new coach rebuild (Phases 0-6) — this audit is only about pre-existing code.
- `coach_*` DB table data — left untouched per plan.
- Any non-coach code accidentally matching keywords (none found; PlayerProfile is the only dual-use term and is KEEP).

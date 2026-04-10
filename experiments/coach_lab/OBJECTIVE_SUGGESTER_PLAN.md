# Coach Lab Objective Suggester Plan

Date: 2026-04-08
Status: Proposed implementation plan

## Goal

Build a Coach Lab workflow that turns a set of reviewed gameplay clips into useful learning objectives without requiring:

- per-clip objective labels
- full-VOD or all-frame training
- expensive end-to-end video training infrastructure

The intended system should use clip-level evidence for perception and bundle-level reasoning for objective selection.

## Current Reality

Observed from the local DB on 2026-04-08:

- `62` total `coach_moments`
- `39` gold manual labels
- `16` reviewed games represented in gold labels
- `0` structured manual `primary_reason` values
- `1` manually attached objective
- `3` saved recommendation rows
- `64` games with `review_notes`
- `35` games with `mistakes`
- `15` games with `focus_next`
- `6` games with `spotted_problems`

Target active models in `coach_models` after the Gemma-only cleanup:

- `gemma_base`
- `gemma_adapter`

Current product behavior target:

- Clip draft labeling runs through Gemma in `src/LoLReview.Core/Services/CoachSidecarClient.cs`.
- Objective suggestion should come from a Gemma bundle planner in `src/LoLReview.Core/Services/CoachRecommendationService.cs`.
- The `Train` button in `src/LoLReview.Core/Services/CoachTrainingService.cs` should register Gemma 4 E4B, prepare the dataset, and optionally fine-tune a Gemma adapter.
- `coach_recommendations` should store draft suggestions plus accept/reject feedback and downstream outcomes.

## Product Principle

Do not treat objective suggestion as a clip classification problem.

Use a staged system:

1. Clip -> structured clip card
2. Recent clip cards + game review context -> objective decision
3. User feedback + later games -> planner supervision

This keeps labeling light and matches the actual data available in Coach Lab.

## Recommended Architecture

### 1. Clip Card Layer

Use Gemma 4 E4B to produce strict JSON for each saved clip:

- `quality`
- `reason_key`
- `confidence`
- `evidence`
- `attached_objective` if obvious, otherwise `null`

This should continue to be clip-first and cached in `coach_inferences`.

### 2. Bundle Planner Layer

Add a new planner step that reasons over a bundle of evidence instead of a single clip.

Planner input should include:

- current objective block
- current objective title and key
- `8-12` recent reviewed clip cards
- clips from `3-5` recent games when available
- game-level review text such as `mistakes`, `focus_next`, `review_notes`, and `spotted_problems`
- a shortlist of objective candidates

Planner output should be strict JSON:

```json
{
  "decision": "keep_current|use_existing|create_new",
  "objective_key": "string|null",
  "title": "string|null",
  "why": "short explanation",
  "evidence_clip_ids": ["1", "2"],
  "confidence": 0.0,
  "follow_up_metric": "string"
}
```

### 3. Candidate Generation Layer

Do not ask the model to invent objectives from an unlimited space.

Generate candidates in C# first:

- `keep_current`
- up to `3` existing objectives related to dominant bad themes
- up to `1-2` new objective candidates derived from recurring blockers

Then let Gemma choose among them.

### 4. Feedback Layer

Store what the system suggested and what the user did:

- accepted
- rejected
- ignored
- applied objective key
- rejection reason
- outcome after `3-5` later games

This is the future planner training dataset.

## What Not To Do

Do not:

- train an end-to-end "raw clips -> objective" model now
- require the user to label each clip with an objective
- train a planner before collecting planner feedback
- move to full-VOD or all-frame pipelines before proving clip bundles are insufficient

## Phase Plan

## Phase 1: Replace Heuristic Objective Suggestion With A Gemma Bundle Planner

Target:

- Ship a better objective suggester without new model training.

Implementation:

- Extend `src/LoLReview.Core/Services/CoachSidecarClient.cs` with a bundle-planning path.
- Reuse the persistent Gemma worker instead of spawning a new Python process per suggestion.
- Rewrite `GenerateObjectiveSuggestionAsync` in `src/LoLReview.Core/Services/CoachRecommendationService.cs` to:
  - load recent clip evidence
  - load related game review text
  - generate a deterministic candidate shortlist
  - call the planner
  - surface a Gemma setup or inference error instead of falling back to a non-Gemma coach path

Needed model behavior:

- prefer multi-game recurring blockers over single-clip noise
- explain why the current objective should be kept or changed
- cite clip ids in the final answer

Success criteria:

- suggestions feel materially smarter than the current heuristic output
- no user-facing latency regression that makes the feature unusable

## Phase 2: Add Recommendation Feedback Storage

Target:

- Start collecting planner supervision.

Implementation:

- Update `src/LoLReview.Core/Data/Schema.cs`.
- Either extend `coach_recommendations` or add a dedicated feedback table.
- Persist:
  - recommendation id
  - candidate list snapshot
  - user decision
  - chosen objective
  - rejection reason
  - evaluation window
  - later outcome summary

UI and service changes:

- Add accept/reject actions in Coach Lab viewmodel and UI flow.
- Save feedback through `src/LoLReview.Core/Services/CoachLabService.cs`.

Success criteria:

- every recommendation can later be audited as accepted, rejected, or ignored

## Phase 3: Normalize Clip Reasons Instead Of Objective Labels

Target:

- Improve clip supervision without forcing objective labeling.

Implementation:

- Define a controlled `reason_key` vocabulary.
- Update manual review/save flows so gold labels prefer a normalized `reason_key`.
- Keep free-text notes, but treat them as secondary evidence.
- Optionally add a light remap tool for historical free-text labels.

Likely files:

- `src/LoLReview.Core/Services/CoachLabService.cs`
- `src/LoLReview.Core/Models/CoachLabModels.cs`
- `src/LoLReview.App/ViewModels/CoachLabViewModel.cs`

Success criteria:

- most future gold labels contain a usable structured reason

## Phase 4: Backfill Bundle-Level Weak Supervision

Target:

- Create useful planner examples without hand-labeling clip objectives.

Implementation:

- Use historical `mistakes`, `focus_next`, `review_notes`, and `spotted_problems` to enrich bundle prompts and offline evaluation.
- Export bundle-ready records from `experiments/coach_lab/export_dataset.py`.
- Create an offline dataset of:
  - recent clip bundles
  - current objective
  - candidate objectives
  - provisional target or later outcome

Success criteria:

- offline planner evaluation is possible without inventing fake clip-objective labels

## Phase 5: Train Only When The Data Justifies It

### 5A. Clip Judge Tuning

Start here first if needed.

Gate:

- at least `200-500` reviewed clips with stable structured `reason_key` values

Approach:

- use LoRA or QLoRA on the clip-card extraction task
- keep planner logic prompted for now

Goal:

- cheaper and more consistent clip-card generation

### 5B. Planner Preference Tuning

Only start after planner feedback exists.

Gate:

- at least dozens of accepted or rejected objective suggestions
- stable candidate generation
- measurable later-game outcomes

Approach:

- preference or ranking tuning on bundle-level planner outputs
- not direct raw-video training

Goal:

- improve keep/switch/create decisions, not basic clip perception

## File-Level Implementation Map

Primary files to change:

- `src/LoLReview.Core/Services/CoachRecommendationService.cs`
  - replace heuristic objective selection with bundle planning orchestration
- `src/LoLReview.Core/Services/CoachSidecarClient.cs`
  - add planner request and response handling through the persistent Qwen worker
- `src/LoLReview.Core/Data/Schema.cs`
  - add recommendation feedback persistence
- `src/LoLReview.Core/Services/CoachLabService.cs`
  - save and load recommendation feedback
- `src/LoLReview.Core/Models/CoachLabModels.cs`
  - add planner and feedback models
- `src/LoLReview.App/ViewModels/CoachLabViewModel.cs`
  - add accept/reject or apply-objective actions
- `experiments/coach_lab/export_dataset.py`
  - export bundle-ready planner data and feedback-ready fields

Potential new files:

- `experiments/coach_lab/plan_objective_bundle.py`
  - optional bundle-planning prompt or local test harness
- `src/LoLReview.Core/Models/CoachPlannerModels.cs`
  - optional dedicated planner contracts

## Evaluation Plan

Measure the system in this order:

1. Planner usefulness
   - user accepts or applies the suggested objective
2. Evidence quality
   - planner cites relevant clips and game notes
3. Outcome quality
   - recurrence of the same blocker drops over the next `3-5` games
4. Stability
   - repeated runs over similar evidence produce similar suggestions
5. Latency
   - suggestion time remains practical for Coach Lab use

## Budget And Performance Notes

Given the current setup:

- local Qwen 7B inference is already available
- a persistent worker is already in place
- full-VOD training would be overkill for the current supervision quality

Best cost/performance path:

- use local Qwen teacher for clip cards and bundle planning
- cache aggressively
- train later, and only on the layer that has sufficient supervision

## Immediate Next Step

Implement Phase 1 first:

- keep the current clip draft path
- add a Qwen bundle planner
- preserve the heuristic path as fallback

This gives a materially better objective suggester before any new training work.

# Coach Lab Bootstrap

Coach Lab is now a Gemma-only workflow.

What exists today:

- hidden WinUI `Coach Lab` page behind `LOLREVIEW_ENABLE_COACH_LAB=1`
- clip-first ingestion from `vod_bookmarks`
- storyboard + minimap artifacts for each saved coach moment
- Gemma-only clip drafting, recurring-problem analysis, and objective planning
- dataset export for clip-card fine-tuning
- recommendation feedback fields for later planner supervision

What does not exist:

- no Qwen, heuristic assist mode, or premature prototype fallback
- no planner fine-tuning path yet
- no end-to-end full-VOD training path

Important:

- launch the app once after pulling these changes so `DatabaseInitializer` creates or migrates the `coach_*` tables
- Gemma setup is now explicit; if no Gemma base model is registered, Coach Lab shows setup/error states instead of falling back

## Enable the hidden page

```powershell
$env:LOLREVIEW_ENABLE_COACH_LAB = "1"
```

Then launch the app and open `Coach Lab` from the sidebar.

## Workflow

1. Save meaningful lane clips in the VOD player and write a useful clip note.
2. Open `Coach Lab`.
3. Click `Sync Moments`.
4. Review the Gemma clip card, attach the right objective if needed, and save the manual label.
5. Click `Find Problems` or `Suggest Objective` once Gemma has scored a few clips.
6. Export the dataset when you want to prepare or run a Gemma fine-tuning pilot.
7. Use `Train Gemma` in the app, or run the scripts below directly.

## Install the Gemma stack

```powershell
.\experiments\coach_lab\setup_gemma_stack.ps1
```

This creates `.venv-coach-gemma` in the repo root and installs the local Gemma inference plus QLoRA training dependencies.
Coach Lab auto-detects that venv on the next app launch.

## Register the Gemma base model

```powershell
python .\experiments\coach_lab\register_gemma_e4b.py --activate
```

This registers `google/gemma-4-E4B-it` as the active Coach Lab base model in `coach_models`.

## Export the dataset

```powershell
python .\experiments\coach_lab\export_dataset.py
```

Optional:

```powershell
python .\experiments\coach_lab\export_dataset.py --gold-only
python .\experiments\coach_lab\export_dataset.py --output C:\temp\coach-dataset
```

The export writes JSONL rows with:

- note-blind runtime input plus artifact paths
- manual labels when present
- Gemma inference when present
- attached objective metadata
- clip notes and game review fields under `supervision`
- summary counts for gold, silver, and bronze supervision buckets

## Prepare or train the Gemma adapter

Prepare only:

```powershell
python .\experiments\coach_lab\train_gemma_e4b.py --prepare-only
```

Prepare and attempt a local QLoRA pilot:

```powershell
python .\experiments\coach_lab\train_gemma_e4b.py --register
```

What this does:

- reads exported clip moments
- keeps the clip-card task only
- writes a Gemma-ready dataset with composite storyboard/minimap images
- attempts a small local Gemma adapter run when dependencies and hardware are available
- optionally registers the trained adapter as `gemma_adapter`

If training is skipped or fails, the base Gemma model remains usable for Coach Lab inference.

## Runtime files

- `gemma_stack.py`: shared Gemma dataset, registration, and inference helpers
- `gemma_worker.py`: persistent worker used by the app for drafts, recurring problems, and objective planning
- `register_gemma_e4b.py`: registers the Gemma base model
- `train_gemma_e4b.py`: prepares or runs the clip-card adapter pilot
- `setup_gemma_stack.ps1`: bootstraps the Gemma-only Python environment

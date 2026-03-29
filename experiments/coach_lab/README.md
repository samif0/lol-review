# Coach Lab Bootstrap

This folder holds the local-only dataset utilities for the hidden `Coach Lab`.

What exists today:

- hidden WinUI `Coach Lab` page behind `LOLREVIEW_ENABLE_COACH_LAB=1`
- clip-first ingestion from `vod_bookmarks`
- auto-sampled lane checkpoints from linked ADC VODs
- assist-mode draft labeling in the app
- premature prototype training from prepared clip moments
- Qwen teacher/base/adapter script scaffolding
- manual label normalization into `coach_labels`
- full-frame storyboard plus minimap strip artifacts for each coach moment
- dataset export for future fine-tuning

What does not exist yet:

- no Qwen or Axolotl Python dependencies are installed by default
- no Windows-native Axolotl training path is expected; use Linux/WSL for real base/adapter fine-tuning

Important:

- the app must be launched once after these changes so `DatabaseInitializer` creates the new `coach_*` tables
- exported training rows are note-blind by default: clip notes stay in supervision metadata instead of the primary runtime payload

## Enable the hidden page

```powershell
$env:LOLREVIEW_ENABLE_COACH_LAB = "1"
```

Then launch the app and open `Coach Lab` from the sidebar.

## Workflow

1. Save meaningful clips in the VOD player and write a clip note.
2. Open `Coach Lab`.
3. Click `Sync Moments`.
4. Review the assist-mode draft, pick the real objective attachment, and save a normalized manual label.
5. Export the dataset once enough moments have accumulated.
6. Click `Train` in Coach Lab to fit a deliberately weak prototype from accepted manual clips.
7. Click `Find Problems` to ask the active prototype for recurring bad themes across the prepared clips.
8. Click `Suggest Objective` to turn the current clip evidence into a premature next-objective suggestion.

## Export dataset

```powershell
python .\experiments\coach_lab\export_dataset.py
```

Optional:

```powershell
python .\experiments\coach_lab\export_dataset.py --gold-only
python .\experiments\coach_lab\export_dataset.py --output C:\temp\coach-dataset
```

This writes JSONL files with:

- note-blind `training_input` payloads with clip/storyboard/minimap artifact paths
- manual labels when present
- assist-mode draft inference
- attached objective metadata from the app objective system
- clip notes and review fields under `supervision`
- `summary.json` with bucket counts

The export is intended as the bridge into future Axolotl/Qwen fine-tuning.

## Premature prototype scripts

These power the hidden `Train` button:

- `train_premature_model.py`
- `predict_premature_model.py`

They intentionally train a very weak local image prototype from storyboard + minimap features.
This is only meant to give you an early feedback loop from prepared clips, not a serious coach model.

## Qwen stack

Shared helpers live in:

- `coach_model_stack.py`

Runtime inference script:

- `predict_qwen_judge.py`

Teacher scripts:

- `register_qwen_teacher.py`
- `draft_with_qwen_teacher.py`

Trainable judge scripts:

- `register_qwen_base_model.py`
- `train_qwen_base_model.py`
- `train_personal_adapter.py`

Dependency bootstrap:

- `setup_qwen_stack.ps1`

### Install Qwen inference dependencies

```powershell
.\experiments\coach_lab\setup_qwen_stack.ps1
```

This installs local teacher/base inference packages into a repo-local venv.
Coach Lab will auto-detect `.venv-coach-qwen` on the next app launch, so the app no longer depends on whichever `python` happens to be on `PATH`.
It does not make Axolotl training work natively on Windows.

### Register a pretrained Qwen base judge

```powershell
python .\experiments\coach_lab\register_qwen_base_model.py
```

Once registered, Coach Lab draft scoring will prefer the active base judge over the heuristic/prototype path.

### Register a Qwen teacher

```powershell
python .\experiments\coach_lab\register_qwen_teacher.py
```

Once registered, Coach Lab draft scoring will prefer the active teacher over the heuristic/prototype path when the Python dependencies are available.

### Run teacher drafts offline

```powershell
python .\experiments\coach_lab\draft_with_qwen_teacher.py
```

That writes `teacher-drafts.jsonl` beside the export.

### Prepare or train the shared base judge

```powershell
python .\experiments\coach_lab\train_qwen_base_model.py --prepare-only
python .\experiments\coach_lab\train_qwen_base_model.py --register
```

What this does:

- builds an Axolotl-ready multimodal dataset from exported clip moments
- writes an Axolotl config
- if Axolotl is installed and supported, runs training
- optionally registers the trained base judge into `coach_models`

### Prepare or train the personal adapter

```powershell
python .\experiments\coach_lab\train_personal_adapter.py --prepare-only
python .\experiments\coach_lab\train_personal_adapter.py --register
```

This uses only gold manual clips and is meant to specialize the judge toward your own labeled moments.

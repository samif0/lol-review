# SAM3 VOD Experiment

This is a local-only sidecar experiment for exploring SAM3 against League of Legends VODs.

It is intentionally not wired into:

- `Revu.sln`
- `src/Revu.App`
- the release workflow
- the shipped settings or navigation UI

The runner integrates with your local LoL Review install by reading:

- `%LOCALAPPDATA%\LoLReviewData\config.json`
- `%LOCALAPPDATA%\LoLReviewData\revu.db`

and by writing analysis artifacts under:

- `%LOCALAPPDATA%\LoLReviewData\vod-analysis\sam3-experiment`

## Local-only gate

The runner refuses to start unless this environment variable is set:

```powershell
$env:LOLREVIEW_ENABLE_SAM3_EXPERIMENT = "1"
```

That keeps the experiment out of the way unless you explicitly opt in on your machine.

## What exists today

- A safe dry-run mode that discovers Ascent VODs and linked VOD rows
- FFprobe-based metadata collection
- Prompt profile loading for League-oriented objects
- Optional preview-frame extraction with `ffmpeg`
- A SAM3 backend that automatically extracts short clip windows before inference so long VODs do not blow up memory

## What does not exist yet

- No public app feature
- No schema changes
- No hidden WinUI page
- No release packaging
- No guarantee that the default prompts are useful without prompt iteration

## Quick start

Build the local Python environment first:

```powershell
.\experiments\sam3_vod\setup_local_env.ps1
```

This installs the upstream SAM3 stack into `experiments/sam3_vod/.venv` and applies the current Windows fallback patches needed for this machine.

Use the helper wrapper:

```powershell
$env:LOLREVIEW_ENABLE_SAM3_EXPERIMENT = "1"
.\experiments\sam3_vod\run_local.ps1 probe --limit 3
```

Create a dry-run job package:

```powershell
$env:LOLREVIEW_ENABLE_SAM3_EXPERIMENT = "1"
.\experiments\sam3_vod\run_local.ps1 plan --limit 2 --extract-previews
```

Attempt real SAM3 inference after you have installed upstream SAM3 and authenticated with Hugging Face:

```powershell
$env:LOLREVIEW_ENABLE_SAM3_EXPERIMENT = "1"
.\experiments\sam3_vod\run_local.ps1 run --backend sam3 --limit 1
```

Track within each short clip window as well:

```powershell
$env:LOLREVIEW_ENABLE_SAM3_EXPERIMENT = "1"
.\experiments\sam3_vod\run_local.ps1 run --backend sam3 --limit 1 --sam3-propagate
```

## Hugging Face auth

Once your model access is approved, log in on this machine with:

```powershell
hf auth login
```

You can verify the stored token with:

```powershell
hf auth whoami
```

## SAM3 setup

This repo does not vendor SAM3. Follow the upstream instructions from:

- https://github.com/facebookresearch/sam3
- https://huggingface.co/facebook/sam3

As of March 27, 2026, the upstream README says SAM3 requires:

- Python 3.12+
- PyTorch 2.7+
- a CUDA-compatible GPU with CUDA 12.6+
- Hugging Face checkpoint access and authentication

This machine appears to satisfy the Python/GPU side already, but checkpoint access is still your responsibility.

## Why clip windows exist

The upstream SAM3 video loader tries to hold the session frames in memory. On raw League VODs that becomes unreasonable very quickly, so this experiment extracts short image-frame windows around each sampled timestamp and runs SAM3 on those windows instead of the full recording.

## Prompt profile

The default profile is:

- `experiments/sam3_vod/profiles/league_default.json`

It starts broad on purpose. Expect to iterate quickly on prompt wording.

Prompt entries can also include manual geometry for local experiments, for example:

```json
{
  "id": "vayne-box",
  "bounding_boxes": [[0.48, 0.10, 0.13, 0.20]],
  "bounding_box_labels": [1]
}
```

Boxes use normalized `[x, y, width, height]` coordinates on the extracted clip frame.

## Output layout

Each run writes a timestamped folder with:

- `manifest.json`
- `results/*.json`
- `previews/*` when preview extraction is enabled

The manifest is the stable contract for future integration work.

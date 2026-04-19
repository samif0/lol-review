# Coach sidecar packaging

The coach sidecar ships as two independent zip packs so users who don't
use the concept-extraction features never pay for PyTorch on disk.

## Pack layout

```
coach-core-<ver>-win-x64.zip        ~40–60 MB  (required)
├── coach.cmd                       launcher — `python -m coach.main`
├── manifest.json                   pack metadata
├── runtime/                        embedded Python 3.12 + core deps
│   ├── python.exe
│   ├── Lib/site-packages/          fastapi, uvicorn, httpx, ...
│   └── python312._pth
└── app/
    └── coach/                      the coach/ Python package

coach-ml-<ver>-win-x64.zip          ~500–800 MB  (optional)
├── manifest.json
└── site-packages/                  torch, sentence-transformers, hdbscan, ...
```

The ML pack has **no Python interpreter**. At startup the core pack's
`coach/_extras.py` probes for the ML pack and, if present, appends its
`site-packages/` to `sys.path`. Concept endpoints (`/concepts/*`) then
import `sentence_transformers` / `hdbscan` lazily and succeed. Without
the ML pack, those endpoints return HTTP 501 with a stable error code
`ml_extras_not_installed` so the C# app can prompt the user.

## Build locally

From the repo root:

```powershell
.\coach\packaging\build-core.ps1                  # ~1–2 min
.\coach\packaging\build-ml.ps1                    # ~5–15 min (big pip install)
```

Output lands in `coach/dist/`:

```
coach-core-0.1.0-win-x64.zip
coach-core-0.1.0-win-x64.sha256
coach-ml-0.1.0-win-x64.zip
coach-ml-0.1.0-win-x64.sha256
```

Pass `-Clean` on either script to force a fresh build (deletes
`coach/build/`).

## Smoke test a built core pack

```powershell
cd coach\dist
Expand-Archive -Path .\coach-core-0.1.0-win-x64.zip -DestinationPath .\_smoke -Force
cd _smoke
.\coach.cmd --port 5577
```

In another shell:

```powershell
curl http://127.0.0.1:5577/health
# 200, status=degraded (no API key), provider=google_ai

curl -X POST http://127.0.0.1:5577/concepts/extract/1
# 501, detail.error=ml_extras_not_installed
```

## Smoke test with the ML pack

Extract the ML pack next to the core pack's `runtime/` dir:

```powershell
cd coach\dist\_smoke
Expand-Archive -Path ..\coach-ml-0.1.0-win-x64.zip -DestinationPath .\ml -Force
.\coach.cmd --port 5577
```

`_extras.py` finds `<pack-root>/ml/site-packages/` as a sibling of
`<pack-root>/runtime/` and activates it. `curl -X POST http://127.0.0.1:5577/concepts/extract/1`
should now succeed (or fail with a domain error — "game 1 not found" —
instead of a 501).

For a custom ML location, set `COACH_ML_DIR` to the dir containing
`site-packages/`.

## Python version

The build scripts pin **Python 3.12.8** via python.org's embeddable
distribution. Don't change this casually: every ML pack built against a
given Python minor version MUST be paired with a core pack of the same
minor version (ABI compatibility, particularly for torch's native
extensions). If you bump the Python version, bump the app version too
and rebuild both packs in lockstep.

Your dev venv (`coach/.venv/`) can be any supported Python; it isn't
involved in packaging.

## Size targets

- **coach-core** goal: ≤ 80 MB. Current core deps + embedded Python
  come in around 50 MB.
- **coach-ml** goal: ≤ 1 GB. CPU-only torch + sentence-transformers +
  hdbscan currently land around 600–750 MB.

If core exceeds target, audit `requirements-core.txt` for deps that
should be in ML instead. If ML balloons, check whether a dep pulled in
a CUDA torch wheel (watch pip's install log for `torch-<ver>+cu*`).

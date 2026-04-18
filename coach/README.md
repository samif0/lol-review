# coach/ — lol-review AI coaching sidecar

Python FastAPI process. Launched by the C# app (`CoachSidecarService.cs`)
like `ffmpeg.exe`. Communicates over HTTP on `localhost:5577` by default.

See `COACH_PLAN.md` at the repo root for the full build plan.

## Install (dev)

```powershell
cd coach
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -e ".[dev]"
```

## Run (dev)

```powershell
python -m coach.main
```

Defaults to `localhost:5577`. Config at `%LOCALAPPDATA%\LoLReviewData\coach_config.json`.

## HTTP API

```
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
POST /coach/test-prompt           { prompt }
```

## Providers

Default is **Google AI Studio** (hosted): zero-setup for users — paste an
API key once, done. No Ollama install, no model pull, no GPU required.

- **Google AI Studio** (default, hosted): `gemini-2.5-flash` — fast,
  multimodal, JSON mode, free tier. Swap to `gemini-2.5-pro` for higher
  quality, or `gemma-4-e4b-it` if/when Google publishes Gemma 4 on the
  Gemini API.
- **Ollama** (local, advanced): `gemma4:e4b` — requires Ollama 0.6+ and
  `ollama pull gemma4:e4b` (~4 GB). Local inference, nothing leaves the
  machine. Other tags: `gemma4:e2b`, `gemma4:26b`, `gemma4:31b`.
- **OpenRouter** (hosted, flexible): `google/gemma-3-27b-it` — pick any
  model OpenRouter supports (Claude, GPT, Gemma, etc.).

Config example at `%LOCALAPPDATA%\LoLReviewData\coach_config.json`:

```json
{
  "provider": "google_ai",
  "port": 5577,
  "ollama":     { "base_url": "http://localhost:11434", "model": "gemma4:e4b", "vision_model": "gemma4:e4b" },
  "google_ai":  { "model": "gemini-2.5-flash" },
  "openrouter": { "model": "google/gemma-3-27b-it" }
}
```

## Getting a Google AI Studio API key

1. Go to https://aistudio.google.com/apikey
2. Click "Create API key" (free tier — no credit card required)
3. Paste the key into the Google AI field in the app's Settings → AI Coach section
4. Click "Save coach config"
5. Click "Send test prompt" to verify

API keys are **not** in the config file — they're injected by C# from
Windows Credential Manager via `POST /config` after sidecar health green.

## Database safety

The sidecar writes to the user's `lol_review.db` but is restricted to
an explicit allowlist of coach-owned tables. Core tables are read-only
from Python. See `coach/db.py`. A pre-migration backup is mandatory on
every startup.

## Data locations

- Config: `%LOCALAPPDATA%\LoLReviewData\coach_config.json`
- Logs: `%LOCALAPPDATA%\LoLReview\coach.log`
- DB backups: `%LOCALAPPDATA%\LoLReviewData\backups\coach-pre-migration-*.db`
- Embeddings: `%LOCALAPPDATA%\LoLReviewData\coach\embeddings\*.parquet`
- Clip frames: `%LOCALAPPDATA%\LoLReviewData\coach_frames\{bookmark_id}\*.png`

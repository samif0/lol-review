# LoL Review

A Windows desktop app that runs in the background and tracks your League of Legends ranked games. Pops up a review window after every game so you can reflect, take notes, and improve over time.

## What it does

- Detects champ select and shows a pre-game focus window — your last review, recent form, and a space to set your gameplay focus for the game
- After the game, pops up your full stats (KDA, CS, vision, damage, etc.) with fields for notes, tags, and mental reflection
- Dashboard on startup recaps your session and flags unreviewed games
- Game history, session logger, loss review, and Claude context export built in
- ARAM and other casual modes are automatically filtered out
- All data stored locally in SQLite — nothing leaves your machine
- Auto-updates when a new release is published

## Install

Download the latest zip from [Releases](https://github.com/samif0/lol-review/releases), extract it anywhere, and run `LoLReview.exe`. No Python needed.

---

## Development

### Requirements

- Windows 10/11
- Python 3.11+ (3.10 minimum)
- League of Legends client (for live monitoring — not required to just run the app)

### Setup

```bash
git clone https://github.com/samif0/lol-review.git
cd lol-review
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

### Run from source

```bash
python run.pyw
```

The app starts in the system tray. Right-click the tray icon for all options.

---

## Building

### Prerequisites

The build script bundles two optional dependencies for the embedded VOD player and clip features. Without them the app still works — VOD playback falls back to the system default player and clip extraction is unavailable.

**ffmpeg** (clip extraction):
```bash
python scripts/download_ffmpeg.py   # if the script exists
# or download manually from https://github.com/BtbN/FFmpeg-Builds/releases
# and place ffmpeg.exe in deps/
```

**libmpv** (embedded VOD player):
```bash
python scripts/download_mpv.py      # if the script exists
# or download libmpv-2.dll from https://github.com/shinchiro/mpv-winbuild-cmake/releases
# (grab the mpv-dev-x86_64-*.7z, extract libmpv-2.dll) and place it in deps/
```

### Build

```bash
pip install pyinstaller
python build.py
```

Output lands in `dist/LoLReview/`. Run `dist/LoLReview/LoLReview.exe` to test the build locally before releasing.

---

## Releasing

Releases are fully automated via GitHub Actions. To publish a new version:

1. **Bump the version** in `src/version.py`:
   ```python
   __version__ = "1.2.0"
   ```

2. **Commit and tag:**
   ```bash
   git add src/version.py
   git commit -m "chore: bump version to 1.2.0"
   git tag v1.2.0
   git push && git push --tags
   ```

The CI pipeline (`.github/workflows/release.yml`) will:
- Stamp `version.py` from the tag before building
- Download ffmpeg and libmpv automatically
- Build the exe with PyInstaller on a Windows runner
- Zip `dist/LoLReview/` and publish it as a GitHub Release

The built exe checks GitHub Releases on startup and auto-updates when a newer version is found.

> **Important:** The version tag must match `__version__` in `version.py`. If they're out of sync, the app will see itself as outdated and restart in a loop. The CI pipeline enforces this by overwriting `version.py` from the tag — so the tag is always the source of truth.

---

## Project structure

```
src/
  main.py             # App entry point, tray icon, monitor wiring
  gui/                # All UI windows (dashboard, review, pregame, history, etc.)
  database/           # SQLite repositories and Claude context generator
  lcu/                # League client API (monitor, credentials, stats parsing)
  vod.py              # VOD auto-matching logic
  updater.py          # Auto-update system
  version.py          # Version string — bump this before tagging a release
build.py              # PyInstaller build script
requirements.txt
```

## Data location

All game data is stored in:
```
%LOCALAPPDATA%\LoLReviewData\lol_review.db
```

Logs are written alongside the database as `lol_review.log`.

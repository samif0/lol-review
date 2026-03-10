# LoL Game Review

A Windows desktop app that automatically tracks your League of Legends game stats and pops up a review window after every game so you can reflect on your performance.

## Features

- **Auto-detection** — Monitors the League client in the background and detects champ select, game start, and game end
- **Pre-game focus window** — Pops up during champ select showing your last review's focus point, recent mistakes to avoid, your win/loss streak, and last 5 game results. Set your intentions before the game starts. Auto-closes when loading screen begins, or dismiss with the "I'm Ready" button
- **Full stat tracking** — Captures everything: KDA, CS, vision, damage breakdown, gold, objectives, multikills, and more
- **Post-game review popup** — A dark-themed window appears after each game with your stats and fields to write notes
- **Review tools** — Star rating, tags (Tilted, Stomped Lane, Comeback, etc.), "what went well", "what to improve", and "focus for next game"
- **Game history** — Browse all past games with stats, reviews, and per-champion breakdowns
- **Local storage** — Everything saved in a local SQLite database. Your data stays on your machine
- **System tray** — Runs quietly in the background with a tray icon showing connection status

## Setup

### Requirements
- Windows 10/11
- Python 3.10+
- League of Legends client must be running

### Install

```bash
cd lol-review
pip install -r requirements.txt
```

### Run

**Option A — No console window (recommended):**
Double-click `run.pyw`

**Option B — With console (for debugging):**
Double-click `run.bat` or run:
```bash
python -m src.main
```

## How It Works

1. Start the app — it sits in your system tray (look for the colored circle icon)
2. Open League of Legends and queue up
3. **During champ select** — a pre-game focus window pops up showing your last game's focus, mistakes to avoid, your streak, and recent form. Set your intention for this game, or pick a quick-focus like "CS better early" or "Track enemy JG"
4. **When loading screen starts** — the pre-game window auto-closes (or click "I'm Ready" to close it early)
5. **When the game ends** — a review window pops up showing all your stats. Fill in notes, rate your performance, tag the game, and click Save
6. Right-click the tray icon → "Game History" to browse past games and stats

## Sharing with Friends

You can build a standalone `.exe` that anyone can run without installing Python.

### Build the exe

```bash
pip install pyinstaller
python build.py
```

This creates `dist/LoLReview/` with everything bundled. Zip that folder and send it. Your friend just unzips and double-clicks `LoLReview.exe`.

### Each person gets their own data

Game data is stored in each user's `%LOCALAPPDATA%\LoLReview\` folder automatically, so multiple people can share the same exe without overwriting each other's reviews.

## Data

All data is stored locally in `%LOCALAPPDATA%\LoLReview\lol_review.db` (SQLite). You can:
- Back it up by copying this file
- Open it with any SQLite browser (like DB Browser for SQLite) for custom queries
- Right-click the tray icon → "Open Data Folder" to find it

## How It Connects to League

The app uses the **League Client Update (LCU) API** — a local REST API that the League client exposes on your machine. No Riot API key is needed and no data is sent anywhere. The app finds the client by:

1. Checking the `LeagueClientUx.exe` process for the auth token and port
2. Falling back to reading the `lockfile` in your League install directory

If the app can't find the client, it just waits quietly and retries every 5 seconds.

## Troubleshooting

**App says "Waiting for League..."**
- Make sure the League client is open (not just the game — the client with your friends list)
- If League is installed somewhere unusual, set `LEAGUE_PATH` environment variable to the install folder

**Review window didn't appear**
- Check the console/log output in `data/lol_review.log`
- The end-of-game data can sometimes take a few seconds to load

**Want to add custom tags?**
- Tags are stored in the SQLite database. You can add new ones directly or modify `database.py`'s `DEFAULT_TAGS` list

## Project Structure

```
lol-review/
├── run.pyw              # Double-click launcher (no console)
├── run.bat              # Console launcher (for debugging)
├── build.py             # PyInstaller build script
├── requirements.txt     # Python dependencies
├── README.md
└── src/
    ├── __init__.py
    ├── __main__.py      # python -m src support
    ├── main.py          # Entry point, tray icon, app orchestration
    ├── lcu.py           # League Client API connection & stat extraction
    ├── database.py      # SQLite storage layer
    └── gui.py           # Review popup & history windows
```

Data is stored per-user at `%LOCALAPPDATA%\LoLReview\` (not in the project folder).

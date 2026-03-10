# LoL Review

A Windows desktop app that runs in the background and tracks your League of Legends ranked games. Pops up a review window after every game so you can reflect, take notes, and improve over time.

## What it does

- Detects champ select, shows a pre-game focus window with your last review and recent form
- After the game, pops up your full stats (KDA, CS, vision, damage, etc.) with fields for notes and tags
- Dashboard on startup recaps yesterday's session and flags unreviewed games
- Game history, session logger, loss review, and Claude context export built in
- ARAM and other casual modes are automatically filtered out
- All data stored locally in SQLite — nothing leaves your machine

## Install

Download the latest zip from [Releases](https://github.com/samif0/lol-review/releases), extract it, and run `LoLReview.exe`. No Python needed.

## Run from source

Requires Windows 10/11, Python 3.10+, and the League client running.

```
pip install -r requirements.txt
python run.pyw
```
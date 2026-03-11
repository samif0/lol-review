"""VOD file discovery and matching for Ascent recordings.

Ascent stores recordings as video files in a configurable folder.
This module scans for recordings and matches them to tracked games
by comparing file timestamps against game start/end times.
"""

import logging
import os
import re
from datetime import datetime
from pathlib import Path
from typing import Optional

from .config import get_ascent_folder

logger = logging.getLogger(__name__)

# Video extensions Ascent might use
_VIDEO_EXTENSIONS = {".mp4", ".mkv", ".avi", ".webm", ".mov"}

# Maximum time gap (seconds) between a game's end and a recording's
# modification time for them to be considered a match.
# Tightened from 600 to 180 to reduce false matches.
_MATCH_WINDOW_S = 180  # 3 minutes


def find_recordings(folder: Optional[str] = None) -> list[dict]:
    """Scan the Ascent folder for video files.

    Returns a list of dicts with keys: path, name, size, mtime, duration_hint.
    Sorted by modification time descending (newest first).
    """
    folder = folder or get_ascent_folder()
    if not folder or not Path(folder).is_dir():
        return []

    recordings = []
    root = Path(folder)

    for item in root.rglob("*"):
        if item.is_file() and item.suffix.lower() in _VIDEO_EXTENSIONS:
            try:
                stat = item.stat()
                recordings.append({
                    "path": str(item),
                    "name": item.name,
                    "size": stat.st_size,
                    "mtime": stat.st_mtime,
                    "mtime_str": datetime.fromtimestamp(stat.st_mtime).strftime(
                        "%Y-%m-%d %H:%M"
                    ),
                })
            except OSError as e:
                logger.warning(f"Could not stat {item}: {e}")

    recordings.sort(key=lambda r: r["mtime"], reverse=True)
    return recordings


def match_recording_to_game(
    recording_mtime: float,
    games: list[dict],
    window_s: int = _MATCH_WINDOW_S,
) -> Optional[dict]:
    """Find the game whose end time is closest to the recording's mtime.

    A recording's mtime should be shortly AFTER the game ends (Ascent
    finishes encoding after the game). We prefer matches where the
    recording mtime is AFTER game end (positive delta) since Ascent
    writes the file after the game. Matches where mtime is before
    game end are penalized.
    """
    best_game = None
    best_score = float("inf")

    for game in games:
        game_ts = game.get("timestamp", 0)
        game_dur = game.get("game_duration", 0)
        if not game_ts:
            continue

        game_end = game_ts + game_dur
        # Positive = recording is after game end (expected)
        # Negative = recording is before game end (unlikely)
        signed_delta = recording_mtime - game_end

        # Only consider recordings that come AFTER the game ended
        # (allow a small 30s grace for clock skew)
        if signed_delta < -30:
            continue

        delta = abs(signed_delta)
        if delta < window_s and delta < best_score:
            best_score = delta
            best_game = game

    return best_game


def auto_match_recordings(games: list[dict], folder: Optional[str] = None) -> list[dict]:
    """Scan for recordings and attempt to match each one to a game.

    Returns a list of dicts: {recording: {...}, game: {...}} for
    successful matches. Games that already have a linked VOD
    (passed in via games with a 'has_vod' key) are skipped.
    """
    recordings = find_recordings(folder)
    if not recordings:
        return []

    # Filter to games that don't already have a VOD
    unmatched_games = [g for g in games if not g.get("has_vod")]
    if not unmatched_games:
        return []

    matches = []
    matched_game_ids = set()

    for rec in recordings:
        game = match_recording_to_game(rec["mtime"], unmatched_games)
        if game and game["game_id"] not in matched_game_ids:
            matches.append({"recording": rec, "game": game})
            matched_game_ids.add(game["game_id"])

    logger.info(f"Auto-matched {len(matches)} recordings to games")
    return matches


def format_game_time(seconds: int) -> str:
    """Format in-game seconds as MM:SS."""
    m, s = divmod(max(0, seconds), 60)
    return f"{m}:{s:02d}"


def parse_game_time(text: str) -> Optional[int]:
    """Parse a MM:SS or M:SS string into seconds. Returns None on failure."""
    text = text.strip()
    match = re.match(r"^(\d{1,3}):(\d{2})$", text)
    if match:
        return int(match.group(1)) * 60 + int(match.group(2))
    # Also accept plain seconds
    if text.isdigit():
        return int(text)
    return None

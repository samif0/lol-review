"""VOD file discovery and matching for Ascent recordings.

Ascent stores recordings as video files in a configurable folder.
Filenames contain the recording start time (e.g. 03-01-2026-14-43.mp4).
This module parses that timestamp and matches it against game start times.
"""

import logging
import re
from datetime import datetime
from pathlib import Path
from typing import Optional

from .config import get_ascent_folder

logger = logging.getLogger(__name__)

# Video extensions Ascent might use
_VIDEO_EXTENSIONS = {".mp4", ".mkv", ".avi", ".webm", ".mov"}

# Ascent filename pattern: M-DD-YYYY-HH-MM or MM-DD-YYYY-HH-MM
_FILENAME_TS_RE = re.compile(r"(\d{1,2})-(\d{1,2})-(\d{4})-(\d{1,2})-(\d{2})")

# Maximum gap (seconds) between the filename timestamp and a game's start
# time for them to be considered a match.  Ascent starts recording around
# the same time the game begins, so a generous 10-minute window covers
# clock skew + loading screen variance.
_MATCH_WINDOW_S = 600  # 10 minutes


def _parse_filename_timestamp(filename: str) -> Optional[float]:
    """Extract a unix timestamp from an Ascent filename like '03-01-2026-14-43.mp4'.

    Returns the timestamp as a float, or None if the filename doesn't match.
    """
    m = _FILENAME_TS_RE.search(filename)
    if not m:
        return None
    try:
        month, day, year, hour, minute = (int(g) for g in m.groups())
        dt = datetime(year, month, day, hour, minute)
        return dt.timestamp()
    except (ValueError, OSError):
        return None


def find_recordings(folder: Optional[str] = None) -> list[dict]:
    """Scan the Ascent folder for video files.

    Returns a list of dicts with keys: path, name, size, mtime, start_ts.
    start_ts is parsed from the filename (preferred for matching).
    Sorted by start_ts descending (newest first), falling back to mtime.
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
                start_ts = _parse_filename_timestamp(item.name)
                recordings.append({
                    "path": str(item),
                    "name": item.name,
                    "size": stat.st_size,
                    "mtime": stat.st_mtime,
                    "start_ts": start_ts,
                    "mtime_str": datetime.fromtimestamp(stat.st_mtime).strftime(
                        "%Y-%m-%d %H:%M"
                    ),
                })
            except OSError as e:
                logger.warning(f"Could not stat {item}: {e}")

    # Sort by start_ts if available, otherwise mtime
    recordings.sort(key=lambda r: r.get("start_ts") or r["mtime"], reverse=True)
    return recordings


def match_recording_to_game(
    recording: dict,
    games: list[dict],
    window_s: int = _MATCH_WINDOW_S,
) -> Optional[dict]:
    """Find the game whose start time best matches the recording.

    Primary strategy: compare the timestamp parsed from the filename
    against each game's start timestamp.  Both represent roughly the
    same moment (game start ≈ recording start).

    Fallback: if the filename has no parseable timestamp, compare the
    file's mtime against the game's end time (old behaviour).
    """
    rec_start = recording.get("start_ts")

    best_game = None
    best_delta = float("inf")

    for game in games:
        game_ts = game.get("timestamp", 0)
        game_dur = game.get("game_duration", 0)
        if not game_ts:
            continue

        if rec_start is not None:
            # Filename-based: compare recording start vs game start.
            # NOTE: game_ts is actually game END time (set at end-of-game),
            # so derive the approximate start by subtracting duration.
            game_start = game_ts - game_dur
            delta = abs(rec_start - game_start)
        else:
            # mtime fallback: compare file mtime vs game end
            game_end = game_ts + game_dur
            signed_delta = recording["mtime"] - game_end
            # Recording should be AFTER game end (allow 30s grace)
            if signed_delta < -30:
                continue
            delta = abs(signed_delta)

        if delta < window_s and delta < best_delta:
            best_delta = delta
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
        game = match_recording_to_game(rec, unmatched_games)
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

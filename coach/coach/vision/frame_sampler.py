"""Extract frames from a VOD clip window using ffmpeg.

Plan §7 Phase 4 task 1. 6 evenly-spaced frames + 1 pre-context + 1 post-context.
"""

from __future__ import annotations

import logging
import subprocess
from pathlib import Path
from typing import Any

from coach.config import frames_dir
from coach.db import fetch_vod_bookmark, fetch_vod_for_game

logger = logging.getLogger(__name__)

FRAMES_EVEN = 6


class VodUnavailableError(Exception):
    """The VOD file is missing or unreadable."""


def sample_frames(bookmark_id: int) -> list[tuple[Path, int]]:
    """Extract frames for one bookmark. Returns [(frame_path, timestamp_ms)]."""
    bookmark = fetch_vod_bookmark(bookmark_id)
    if bookmark is None:
        raise ValueError(f"No bookmark id={bookmark_id}")

    game_id = bookmark["game_id"]
    vod = fetch_vod_for_game(game_id)
    if vod is None or not vod.get("file_path"):
        raise VodUnavailableError(f"No VOD linked for game {game_id}")

    vod_path = Path(vod["file_path"])
    if not vod_path.exists():
        raise VodUnavailableError(f"VOD missing at {vod_path}")

    clip_start_s = int(bookmark.get("clip_start_s") or bookmark.get("game_time_s") or 0)
    clip_end_s = int(bookmark.get("clip_end_s") or clip_start_s + 10)
    if clip_end_s <= clip_start_s:
        clip_end_s = clip_start_s + 10

    out_dir = frames_dir(bookmark_id)
    out_dir.mkdir(parents=True, exist_ok=True)

    # Compute timestamps: pre-context, 6 evenly spaced, post-context.
    timestamps_s: list[float] = []
    timestamps_s.append(max(0.0, clip_start_s - 2.0))
    span = clip_end_s - clip_start_s
    for i in range(FRAMES_EVEN):
        timestamps_s.append(clip_start_s + (i + 0.5) * span / FRAMES_EVEN)
    timestamps_s.append(clip_end_s + 2.0)

    results: list[tuple[Path, int]] = []
    for t in timestamps_s:
        ts_ms = int(t * 1000)
        frame_path = out_dir / f"frame_{ts_ms:08d}.png"
        if frame_path.exists():
            results.append((frame_path, ts_ms))
            continue
        try:
            _extract_one(vod_path, t, frame_path)
            results.append((frame_path, ts_ms))
        except subprocess.CalledProcessError as exc:
            logger.warning("ffmpeg frame extract failed at t=%.2f: %s", t, exc)

    return results


def _extract_one(vod_path: Path, t_s: float, out: Path) -> None:
    """Invoke ffmpeg to grab a single frame."""
    cmd = [
        "ffmpeg",
        "-hide_banner",
        "-loglevel",
        "error",
        "-ss",
        str(t_s),
        "-i",
        str(vod_path),
        "-frames:v",
        "1",
        "-q:v",
        "2",
        "-y",
        str(out),
    ]
    subprocess.run(cmd, check=True, timeout=30)

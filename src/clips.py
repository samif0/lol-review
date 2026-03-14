"""Clip extraction and folder management.

Cuts short clips from VOD recordings using ffmpeg and manages
the clips folder to stay within a configurable size limit.
"""

import logging
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Optional, Tuple, Union

from .config import get_clips_folder, get_clips_max_size_mb
from .constants import FFMPEG_CRF, FFMPEG_CLIP_TIMEOUT_S, FFMPEG_RE_ENCODE_TIMEOUT_S

logger = logging.getLogger(__name__)


def _find_ffmpeg() -> Optional[str]:
    """Locate ffmpeg executable. Checks bundled location, PATH, and common installs."""
    # 1. Check next to the running exe (PyInstaller bundle)
    if getattr(sys, "frozen", False):
        bundled = Path(sys.executable).parent / "ffmpeg.exe"
        if bundled.exists():
            return str(bundled)
        # Also check _MEIPASS (PyInstaller temp extraction dir)
        meipass = getattr(sys, "_MEIPASS", None)
        if meipass:
            meipass_ffmpeg = Path(meipass) / "ffmpeg.exe"
            if meipass_ffmpeg.exists():
                return str(meipass_ffmpeg)

    # 2. Check PATH
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg:
        return ffmpeg

    # 3. Check common Windows locations
    common_paths = [
        Path(os.environ.get("LOCALAPPDATA", "")) / "LoLReview" / "ffmpeg.exe",
        Path(os.environ.get("PROGRAMFILES", "")) / "ffmpeg" / "bin" / "ffmpeg.exe",
        Path(__file__).parent.parent / "deps" / "ffmpeg.exe",
    ]
    for p in common_paths:
        if p.exists():
            return str(p)

    return None


def extract_clip(
    vod_path: str,
    start_s: float,
    end_s: float,
    game_id: int,
    champion_name: str = "",
    note: str = "",
) -> Tuple[Optional[str], str]:
    """Extract a clip from a VOD file using ffmpeg.

    Returns (output_path, error_message).
    On success error_message is "". On failure output_path is None.
    """
    ffmpeg = _find_ffmpeg()
    if not ffmpeg:
        msg = "ffmpeg not found — install ffmpeg or add it to PATH"
        logger.error(msg)
        return None, msg

    if end_s <= start_s:
        msg = f"Invalid clip range: {start_s}s to {end_s}s"
        logger.error(msg)
        return None, msg

    if not Path(vod_path).exists():
        msg = f"VOD file not found: {vod_path}"
        logger.error(msg)
        return None, msg

    clips_dir = get_clips_folder()
    if not clips_dir:
        msg = "No clips folder configured"
        logger.error(msg)
        return None, msg

    Path(clips_dir).mkdir(parents=True, exist_ok=True)

    # Build a descriptive filename — keep the same extension as the source
    # so stream copy doesn't hit container mismatches
    duration = int(end_s - start_s)
    start_mm_ss = f"{int(start_s) // 60}-{int(start_s) % 60:02d}"
    # Strip anything that isn't alphanumeric, dash, or underscore
    import re
    safe_champ = re.sub(r"[^A-Za-z0-9_-]", "", champion_name.replace(" ", "_")) if champion_name else "clip"
    safe_champ = safe_champ or "clip"
    timestamp_str = time.strftime("%Y%m%d_%H%M%S")
    source_ext = Path(vod_path).suffix.lower() or ".mp4"
    filename = f"{safe_champ}_{start_mm_ss}_{duration}s_{timestamp_str}{source_ext}"
    output_path = str(Path(clips_dir) / filename)

    # Run ffmpeg at below-normal priority so it doesn't choke the system
    creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    if sys.platform == "win32":
        creation_flags |= 0x00004000  # BELOW_NORMAL_PRIORITY_CLASS

    # Attempt 1: Stream copy (fast, no CPU load, keyframe-aligned cuts)
    clip_result = _run_ffmpeg_clip(
        ffmpeg, vod_path, start_s, end_s, output_path,
        extra_args=["-c", "copy", "-avoid_negative_ts", "make_zero"],
        creation_flags=creation_flags,
    )
    if clip_result[0]:
        enforce_clips_folder_limit()
        return clip_result

    logger.warning(f"Stream copy failed, falling back to re-encode: {clip_result[1]}")

    # Attempt 2: Lightweight re-encode (handles codec/container mismatches)
    # ultrafast preset + low thread count = minimal CPU impact
    clip_result = _run_ffmpeg_clip(
        ffmpeg, vod_path, start_s, end_s, output_path,
        extra_args=[
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", str(FFMPEG_CRF),
            "-c:a", "aac", "-b:a", "128k",
            "-threads", "2",
            "-movflags", "+faststart",
        ],
        creation_flags=creation_flags,
        timeout=FFMPEG_RE_ENCODE_TIMEOUT_S,  # re-encode takes longer
    )
    if clip_result[0]:
        enforce_clips_folder_limit()
    return clip_result


def _run_ffmpeg_clip(
    ffmpeg: str,
    vod_path: str,
    start_s: float,
    end_s: float,
    output_path: str,
    extra_args: list,
    creation_flags: int = 0,
    timeout: int = FFMPEG_CLIP_TIMEOUT_S,
) -> Tuple[Optional[str], str]:
    """Run a single ffmpeg clip extraction attempt.

    Returns (output_path, "") on success or (None, error_msg) on failure.
    """
    cmd = [
        ffmpeg,
        "-y",                       # Overwrite if exists
        "-ss", str(start_s),        # Seek to start (fast input seek)
        "-i", vod_path,             # Input file
        "-t", str(end_s - start_s), # Duration
        *extra_args,
        output_path,
    ]

    try:
        cmd_str = " ".join(f'"{c}"' if " " in c else c for c in cmd)
        logger.info(f"ffmpeg cmd: {cmd_str}")

        result = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
            creationflags=creation_flags,
        )

        # Decode stderr as utf-8, falling back to latin-1
        try:
            stderr_text = result.stderr.decode("utf-8", errors="replace")
        except Exception:
            stderr_text = str(result.stderr)

        if result.returncode != 0:
            stderr_tail = stderr_text[-500:] if stderr_text.strip() else "(empty stderr)"
            msg = (
                f"ffmpeg exited with code {result.returncode}\n\n"
                f"Command:\n{cmd_str}\n\n"
                f"stderr:\n{stderr_tail}"
            )
            logger.error(f"ffmpeg failed (rc={result.returncode}): {stderr_tail}")
            # Clean up partial output
            try:
                Path(output_path).unlink(missing_ok=True)
            except OSError:
                pass
            return None, msg

        if Path(output_path).exists() and Path(output_path).stat().st_size > 0:
            logger.info(f"Clip saved: {output_path}")
            return output_path, ""
        else:
            msg = (
                f"ffmpeg returned 0 but no output file was created\n\n"
                f"Command:\n{cmd_str}\n\n"
                f"stderr:\n{stderr_text[-500:] if stderr_text.strip() else '(empty)'}"
            )
            logger.error(msg)
            return None, msg

    except subprocess.TimeoutExpired:
        msg = f"ffmpeg timed out after {timeout}s"
        logger.error(msg)
        try:
            Path(output_path).unlink(missing_ok=True)
        except OSError:
            pass
        return None, msg
    except FileNotFoundError:
        msg = f"ffmpeg not found at: {ffmpeg}"
        logger.error(msg)
        return None, msg
    except Exception as e:
        msg = f"Clip extraction failed: {type(e).__name__}: {e}"
        logger.error(msg)
        return None, msg


def get_clips_folder_size_mb() -> float:
    """Return the total size of the clips folder in MB."""
    clips_dir = get_clips_folder()
    if not clips_dir or not Path(clips_dir).is_dir():
        return 0.0

    total = 0
    for f in Path(clips_dir).iterdir():
        if f.is_file():
            total += f.stat().st_size

    return total / (1024 * 1024)


def enforce_clips_folder_limit():
    """Delete oldest clips until the folder is under the size limit.

    Clips are deleted oldest-first (by file modification time).
    """
    clips_dir = get_clips_folder()
    max_mb = get_clips_max_size_mb()

    if not clips_dir or not Path(clips_dir).is_dir():
        return

    # Gather all clip files sorted by mtime (oldest first)
    files = []
    for f in Path(clips_dir).iterdir():
        if f.is_file() and f.suffix.lower() in (".mp4", ".mkv", ".avi", ".webm"):
            files.append((f, f.stat().st_mtime, f.stat().st_size))

    files.sort(key=lambda x: x[1])  # oldest first

    total_bytes = sum(s for _, _, s in files)
    max_bytes = max_mb * 1024 * 1024

    deleted = 0
    while total_bytes > max_bytes and files:
        oldest_file, _, size = files.pop(0)
        try:
            oldest_file.unlink()
            total_bytes -= size
            deleted += 1
            logger.info(f"Deleted old clip to free space: {oldest_file.name}")
        except OSError as e:
            logger.warning(f"Could not delete clip {oldest_file}: {e}")

    if deleted:
        logger.info(
            f"Clips cleanup: deleted {deleted} file(s), "
            f"folder now {total_bytes / (1024*1024):.1f} MB / {max_mb} MB"
        )


def is_ffmpeg_available() -> bool:
    """Check if ffmpeg is available on this system."""
    return _find_ffmpeg() is not None

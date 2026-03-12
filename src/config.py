"""Shared application configuration stored in %LOCALAPPDATA%\LoLReview\config.json.

Handles settings for auto-updates (GitHub token) and optional features
like Ascent VOD integration (recording folder path).
"""

import json
import logging
import os
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

_CONFIG_DIR = Path(
    os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")
) / "LoLReview"
_CONFIG_FILE = _CONFIG_DIR / "config.json"


def _load_config() -> dict:
    """Load the full config dict, returning {} on any failure."""
    try:
        if _CONFIG_FILE.exists():
            return json.loads(_CONFIG_FILE.read_text(encoding="utf-8"))
    except Exception as e:
        logger.warning(f"Could not read config: {e}")
    return {}


def _save_config(config: dict):
    """Write the config dict to disk."""
    _CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    _CONFIG_FILE.write_text(json.dumps(config, indent=2), encoding="utf-8")


# ── GitHub token (used by updater) ───────────────────────────────


def load_github_token() -> str:
    """Load the GitHub token from config, if set."""
    return _load_config().get("github_token", "")


def save_github_token(token: str):
    """Save a GitHub token to config."""
    config = _load_config()
    config["github_token"] = token
    _save_config(config)
    logger.info("GitHub token saved to config")


# ── Ascent VOD settings ──────────────────────────────────────────


def get_ascent_folder() -> Optional[str]:
    """Return the configured Ascent recordings folder, or None if not set."""
    path = _load_config().get("ascent_folder", "")
    if path and Path(path).is_dir():
        return path
    return None


def set_ascent_folder(path: str):
    """Save the Ascent recordings folder path."""
    config = _load_config()
    config["ascent_folder"] = path
    _save_config(config)
    logger.info(f"Ascent folder set to: {path}")


def is_ascent_enabled() -> bool:
    """True if the user has configured a valid Ascent folder."""
    return get_ascent_folder() is not None


# ── VOD keybind settings ─────────────────────────────────────────

# Each action maps to a tkinter key-event string.
# Users can remap these in Settings → Keybinds.
DEFAULT_KEYBINDS: dict[str, str] = {
    "play_pause":    "space",
    "seek_fwd_5":    "Right",
    "seek_back_5":   "Left",
    "seek_fwd_2":    "Shift-Right",
    "seek_back_2":   "Shift-Left",
    "seek_fwd_10":   "Control-Right",
    "seek_back_10":  "Control-Left",
    "seek_fwd_1":    "Alt-Right",
    "seek_back_1":   "Alt-Left",
    "bookmark":      "b",
    "speed_up":      "bracketright",
    "speed_down":    "bracketleft",
    "clip_in":       "i",
    "clip_out":      "o",
}

# Human-readable labels for the settings UI
KEYBIND_LABELS: dict[str, str] = {
    "play_pause":    "Play / Pause",
    "seek_fwd_5":    "Forward 5s",
    "seek_back_5":   "Back 5s",
    "seek_fwd_2":    "Forward 2s",
    "seek_back_2":   "Back 2s",
    "seek_fwd_10":   "Forward 10s",
    "seek_back_10":  "Back 10s",
    "seek_fwd_1":    "Forward 1s",
    "seek_back_1":   "Back 1s",
    "bookmark":      "Bookmark",
    "speed_up":      "Speed Up",
    "speed_down":    "Speed Down",
    "clip_in":       "Clip In",
    "clip_out":      "Clip Out",
}


def get_keybinds() -> dict[str, str]:
    """Return the full keybind map, falling back to defaults for missing keys."""
    saved = _load_config().get("keybinds", {})
    merged = dict(DEFAULT_KEYBINDS)
    for action, key in saved.items():
        if action in merged and key:
            merged[action] = key
    return merged


def set_keybinds(binds: dict[str, str]):
    """Save the keybind map. Only stores non-default values."""
    config = _load_config()
    config["keybinds"] = binds
    _save_config(config)
    logger.info("Keybinds saved")


# ── Clip settings ─────────────────────────────────────────────────

DEFAULT_CLIPS_MAX_SIZE_MB = 2048  # 2 GB default


def get_clips_folder() -> Optional[str]:
    """Return the configured clips folder, or a default under AppData."""
    config = _load_config()
    path = config.get("clips_folder", "")
    if path and Path(path).is_dir():
        return path
    # Default: LoLReview/clips next to the config dir
    default = _CONFIG_DIR / "clips"
    default.mkdir(parents=True, exist_ok=True)
    return str(default)


def set_clips_folder(path: str):
    """Save the clips folder path."""
    config = _load_config()
    config["clips_folder"] = path
    _save_config(config)
    logger.info(f"Clips folder set to: {path}")


def get_clips_max_size_mb() -> int:
    """Return the max total size (MB) for the clips folder."""
    return _load_config().get("clips_max_size_mb", DEFAULT_CLIPS_MAX_SIZE_MB)


def set_clips_max_size_mb(size_mb: int):
    """Save the max clips folder size in MB."""
    config = _load_config()
    config["clips_max_size_mb"] = max(100, size_mb)  # minimum 100 MB
    _save_config(config)
    logger.info(f"Clips max size set to: {size_mb} MB")

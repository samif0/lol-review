"""Auto-update system using GitHub Releases.

On startup the app checks for a newer release. If found, it automatically
downloads, swaps the files in place, and relaunches. The whole process
is seamless — no user interaction, no batch scripts, no console windows.

The approach:
1. Download the release zip to a temp file
2. Extract to a temp folder
3. Rename the running exe from LoLReview.exe → LoLReview.exe.old
   (Windows allows renaming a running exe, just not overwriting it)
4. Copy all new files over the app directory
5. Launch the new exe
6. Exit the old process

On next launch, the app cleans up the .old file.

How to publish an update:
1. Bump __version__ in version.py
2. Build with: .venv\Scripts\python.exe build.py
3. Zip the dist/LoLReview folder
4. Create a GitHub Release with the tag matching the version (e.g. v1.1.0)
5. Attach the zip to the release
"""

import json
import logging
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import zipfile
from pathlib import Path
from typing import Callable, Optional, Tuple

import requests

from .version import __version__, GITHUB_REPO

logger = logging.getLogger(__name__)

# GitHub API endpoint for latest release
_RELEASES_URL = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"

# Config file lives next to the database in %LOCALAPPDATA%\LoLReview\
_CONFIG_DIR = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")) / "LoLReview"
_CONFIG_FILE = _CONFIG_DIR / "config.json"


# ── Config helpers ───────────────────────────────────────────────────


def _load_github_token() -> str:
    """Load the GitHub token from the local config file, if it exists."""
    try:
        if _CONFIG_FILE.exists():
            data = json.loads(_CONFIG_FILE.read_text(encoding="utf-8"))
            return data.get("github_token", "")
    except Exception as e:
        logger.warning(f"Could not read config: {e}")
    return ""


def save_github_token(token: str):
    """Save a GitHub token to the local config file."""
    _CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    config = {}
    try:
        if _CONFIG_FILE.exists():
            config = json.loads(_CONFIG_FILE.read_text(encoding="utf-8"))
    except Exception:
        pass
    config["github_token"] = token
    _CONFIG_FILE.write_text(json.dumps(config, indent=2), encoding="utf-8")
    logger.info("GitHub token saved to config")


def _get_auth_headers() -> dict:
    """Build request headers, with auth token if available."""
    headers = {"Accept": "application/vnd.github.v3+json"}
    token = _load_github_token()
    if token:
        headers["Authorization"] = f"token {token}"
    return headers


def parse_version(version_str: str) -> Tuple[int, ...]:
    """Parse a version string like '1.2.3' or 'v1.2.3' into a comparable tuple."""
    cleaned = version_str.lstrip("vV").strip()
    try:
        return tuple(int(x) for x in cleaned.split("."))
    except (ValueError, AttributeError):
        return (0, 0, 0)


# ── Cleanup from previous update ────────────────────────────────────


def cleanup_old_exe():
    """Delete LoLReview.exe.old left over from a previous update."""
    if not getattr(sys, "frozen", False):
        return
    old_exe = Path(sys.executable).parent / (Path(sys.executable).name + ".old")
    if old_exe.exists():
        try:
            old_exe.unlink()
            logger.info(f"Cleaned up old exe: {old_exe}")
        except Exception as e:
            logger.warning(f"Could not delete old exe: {e}")


# ── Check ────────────────────────────────────────────────────────────


def check_for_update() -> Optional[dict]:
    """Check GitHub Releases for a newer version.

    Returns a dict with update info if available, None otherwise.
    """
    try:
        logger.info(f"Checking for updates at {_RELEASES_URL}")
        headers = _get_auth_headers()
        if "Authorization" in headers:
            logger.info("Using GitHub token for auth")
        else:
            logger.info("No GitHub token — will fail for private repos")

        resp = requests.get(_RELEASES_URL, headers=headers, timeout=10)
        logger.info(f"Update check response: HTTP {resp.status_code}")

        if resp.status_code == 404:
            logger.info("No releases found on GitHub (404)")
            return None

        resp.raise_for_status()
        data = resp.json()

        latest_tag = data.get("tag_name", "")
        latest_version = parse_version(latest_tag)
        current_version = parse_version(__version__)

        if latest_version <= current_version:
            logger.info(f"Up to date (current: {__version__}, latest: {latest_tag})")
            return None

        # Find the zip asset
        download_url = ""
        for asset in data.get("assets", []):
            if asset["name"].endswith(".zip"):
                download_url = asset["browser_download_url"]
                break

        release_url = data.get("html_url", "")

        return {
            "version": latest_tag,
            "download_url": download_url,
            "release_url": release_url,
            "release_notes": data.get("body", ""),
        }

    except requests.RequestException as e:
        logger.warning(f"Update check failed (network error): {e}")
        return None
    except Exception as e:
        logger.warning(f"Update check failed: {e}")
        return None


def check_for_update_async(callback: Callable[[Optional[dict]], None]):
    """Check for updates in a background thread."""
    def _worker():
        result = check_for_update()
        callback(result)
    threading.Thread(target=_worker, daemon=True).start()


# ── Download & Install ───────────────────────────────────────────────


def download_and_install(
    download_url: str,
    on_progress: Optional[Callable[[int, int], None]] = None,
    on_done: Optional[Callable[[bool, str], None]] = None,
):
    """Download the update zip, swap files in place, and relaunch.

    on_progress(downloaded_bytes, total_bytes) — called during download.
    on_done(success, message) — called when finished or on error.

    This runs in a background thread.
    """
    def _worker():
        try:
            _do_download_and_install(download_url, on_progress)
            if on_done:
                on_done(True, "Update ready — restarting...")
        except Exception as e:
            logger.error(f"Update failed: {e}", exc_info=True)
            if on_done:
                on_done(False, str(e))

    threading.Thread(target=_worker, daemon=True).start()


def _do_download_and_install(
    download_url: str,
    on_progress: Optional[Callable[[int, int], None]] = None,
):
    """Download zip, extract, rename running exe, copy new files over."""
    headers = _get_auth_headers()
    dl_headers = {**headers, "Accept": "application/octet-stream"}

    logger.info(f"Downloading update from {download_url}")

    # ── Download ─────────────────────────────────────────────
    resp = requests.get(download_url, headers=dl_headers, stream=True, timeout=60)
    resp.raise_for_status()

    total = int(resp.headers.get("content-length", 0))
    tmp_zip = Path(tempfile.mktemp(suffix=".zip", prefix="lolreview_update_"))

    downloaded = 0
    with open(tmp_zip, "wb") as f:
        for chunk in resp.iter_content(chunk_size=64 * 1024):
            f.write(chunk)
            downloaded += len(chunk)
            if on_progress:
                on_progress(downloaded, total)

    logger.info(f"Downloaded {downloaded} bytes to {tmp_zip}")

    # ── Extract ──────────────────────────────────────────────
    tmp_extract = Path(tempfile.mkdtemp(prefix="lolreview_extract_"))
    with zipfile.ZipFile(tmp_zip, "r") as zf:
        zf.extractall(tmp_extract)

    logger.info(f"Extracted to {tmp_extract}")

    # Find the app folder inside the zip (may be nested in a subfolder)
    extracted_items = list(tmp_extract.iterdir())
    if len(extracted_items) == 1 and extracted_items[0].is_dir():
        source_dir = extracted_items[0]
    else:
        source_dir = tmp_extract

    # ── Swap files ───────────────────────────────────────────
    if getattr(sys, "frozen", False):
        app_dir = Path(sys.executable).parent
        exe_name = Path(sys.executable).name
        old_exe = app_dir / (exe_name + ".old")

        # Rename running exe so we can overwrite it
        logger.info(f"Renaming {exe_name} → {exe_name}.old")
        if old_exe.exists():
            old_exe.unlink()
        os.rename(app_dir / exe_name, old_exe)

        # Copy all new files over the app directory
        logger.info(f"Copying new files from {source_dir} → {app_dir}")
        _copy_tree(source_dir, app_dir)

        # Launch the new exe
        new_exe = app_dir / exe_name
        logger.info(f"Launching new exe: {new_exe}")
        subprocess.Popen(
            [str(new_exe)],
            creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
            close_fds=True,
        )
    else:
        # Running from source — just copy files (for dev testing)
        app_dir = Path(__file__).resolve().parent.parent
        logger.info(f"Dev mode: copying new files from {source_dir} → {app_dir}")
        _copy_tree(source_dir, app_dir)

    # ── Cleanup temp files ───────────────────────────────────
    try:
        tmp_zip.unlink()
    except Exception:
        pass
    try:
        shutil.rmtree(tmp_extract)
    except Exception:
        pass

    logger.info("Update complete — new process launched")


def _copy_tree(src: Path, dst: Path):
    """Recursively copy src into dst, overwriting existing files."""
    for item in src.iterdir():
        target = dst / item.name
        if item.is_dir():
            target.mkdir(exist_ok=True)
            _copy_tree(item, target)
        else:
            try:
                shutil.copy2(item, target)
            except PermissionError:
                # Skip files that are locked (e.g. DLLs in use)
                logger.warning(f"Skipped locked file: {target}")
            except Exception as e:
                logger.warning(f"Failed to copy {item.name}: {e}")

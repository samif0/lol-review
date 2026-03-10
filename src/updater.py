"""Auto-update system using GitHub Releases.

On startup the app checks for a newer release. If found, the user clicks
"Install Update" on the dashboard banner. The updater then:
1. Downloads the zip from the GitHub release
2. Extracts to a temp folder
3. Writes a batch script that waits for this process to exit,
   copies the new files over the old ones, and relaunches the exe
4. Exits the app — the batch script takes over

How to publish an update:
1. Bump __version__ in version.py
2. Build with build.py
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


def _get_app_dir() -> Path:
    """Get the directory the current exe or script lives in."""
    if getattr(sys, "frozen", False):
        # Running as PyInstaller exe
        return Path(sys.executable).parent
    else:
        # Running from source — use the project root
        return Path(__file__).resolve().parent.parent


def _get_exe_path() -> str:
    """Get the path to relaunch after update."""
    if getattr(sys, "frozen", False):
        return str(Path(sys.executable))
    else:
        # Running from source — restart with python
        return f'"{sys.executable}" -m src'


def download_and_install(
    download_url: str,
    on_progress: Optional[Callable[[int, int], None]] = None,
    on_done: Optional[Callable[[bool, str], None]] = None,
):
    """Download the update zip, extract, and replace the app files.

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
    """The actual download + install logic (runs in background thread)."""
    headers = _get_auth_headers()
    # For private repos, asset downloads need Accept: application/octet-stream
    dl_headers = {**headers, "Accept": "application/octet-stream"}

    logger.info(f"Downloading update from {download_url}")

    # Download the zip to a temp file
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

    # Extract to a temp directory
    tmp_extract = Path(tempfile.mkdtemp(prefix="lolreview_extract_"))
    with zipfile.ZipFile(tmp_zip, "r") as zf:
        zf.extractall(tmp_extract)

    logger.info(f"Extracted to {tmp_extract}")

    # Find the actual app folder inside the zip
    # Could be directly in the zip root or inside a subfolder like "LoLReview/"
    extracted_items = list(tmp_extract.iterdir())
    if len(extracted_items) == 1 and extracted_items[0].is_dir():
        source_dir = extracted_items[0]
    else:
        source_dir = tmp_extract

    # Write the swap-and-restart batch script
    app_dir = _get_app_dir()
    exe_path = _get_exe_path()

    _write_update_script(source_dir, app_dir, exe_path, tmp_zip, tmp_extract)

    # Clean up zip (extract dir cleaned by batch script)
    try:
        tmp_zip.unlink()
    except Exception:
        pass


def _write_update_script(
    source_dir: Path, app_dir: Path, exe_path: str,
    tmp_zip: Path, tmp_extract: Path,
):
    """Write and launch a batch script that swaps files and restarts the app.

    The script:
    1. Waits for the current process to exit
    2. Copies new files over the old ones
    3. Cleans up temp files
    4. Relaunches the app
    """
    script_path = Path(tempfile.mktemp(suffix=".bat", prefix="lolreview_update_"))

    pid = os.getpid()

    # Use robocopy /MIR to mirror the new folder over the old one
    # /MIR = mirror (copies new, deletes old files not in source)
    # /NFL /NDL /NJH /NJS = quiet output
    bat_content = f"""@echo off
echo Waiting for LoL Review to close...
:waitloop
tasklist /FI "PID eq {pid}" 2>NUL | find /I "{pid}" >NUL
if not errorlevel 1 (
    timeout /t 1 /nobreak >NUL
    goto waitloop
)

echo Applying update...
robocopy "{source_dir}" "{app_dir}" /MIR /NFL /NDL /NJH /NJS /R:3 /W:2

echo Cleaning up...
rmdir /s /q "{tmp_extract}" 2>NUL

echo Restarting LoL Review...
start "" {exe_path}

del "%~f0"
"""

    script_path.write_text(bat_content, encoding="utf-8")
    logger.info(f"Update script written to {script_path}")

    # Launch the batch script hidden (no console window)
    subprocess.Popen(
        ["cmd", "/c", str(script_path)],
        creationflags=subprocess.CREATE_NO_WINDOW,
        close_fds=True,
    )
    logger.info("Update script launched — exiting app for update")

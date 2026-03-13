"""Auto-update system using GitHub Releases.

On startup the app checks for a newer release. If found, it automatically
downloads, swaps the files in place, and relaunches. The whole process
is seamless — no user interaction, no batch scripts, no console windows.

The approach:
1. Download the release zip to a temp file
2. Extract to a temp folder
3. Write an update lock file recording the target version
4. Rename the running exe from LoLReview.exe → LoLReview.exe.old
   (Windows allows renaming a running exe, just not overwriting it)
5. Copy all new files over the app directory
6. Launch the new exe
7. Exit the old process

On next launch, the app reads the lock file:
- If the lock exists, we just updated — skip the update check this session
  to prevent infinite update loops (lock is deleted after being read).
- The app also cleans up the .old exe file.

How to publish an update (manual):
1. Bump __version__ in version.py  ← MUST match the release tag or loop occurs
2. Build with: .venv\\Scripts\\python.exe build.py
3. Zip the dist/LoLReview folder
4. Create a GitHub Release tagged v{version} (e.g. v1.2.0)
5. Attach the zip to the release

For automated releases, push a version tag — GitHub Actions handles the rest.
"""

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

from .config import load_github_token, save_github_token  # noqa: F401 — re-export
from .version import __version__, GITHUB_REPO

logger = logging.getLogger(__name__)

# GitHub API endpoint for latest release
_RELEASES_URL = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"


def _get_auth_headers() -> dict:
    """Build request headers, with auth token if available."""
    headers = {"Accept": "application/vnd.github.v3+json"}
    token = load_github_token()
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


# ── Update lock (prevents infinite restart loops) ────────────────────


def _get_lock_path() -> Path:
    """Path to the update-pending lock file, written before relaunching."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent / ".update_pending"
    return Path(tempfile.gettempdir()) / "lolreview_update_pending"


def post_update_startup() -> bool:
    """Run on every startup. Reads and clears the update lock if present.

    Returns True if we just completed an update — caller should skip the
    update check this session to prevent infinite restart loops.
    """
    # Always clean up the .old exe first
    if getattr(sys, "frozen", False):
        old_exe = Path(sys.executable).parent / (Path(sys.executable).name + ".old")
        if old_exe.exists():
            try:
                old_exe.unlink()
                logger.info(f"Cleaned up old exe: {old_exe}")
            except Exception as e:
                logger.warning(f"Could not delete old exe: {e}")

    # Remove any stale Python DLLs from a previous Python version.
    # These get left behind when the update copy phase skips locked files.
    _cleanup_old_python_dlls()

    # Check for update lock
    lock = _get_lock_path()
    if not lock.exists():
        return False

    try:
        expected_version = lock.read_text(encoding="utf-8").strip()
        lock.unlink()
    except Exception as e:
        logger.warning(f"Could not read/clear update lock: {e}")
        return True  # Assume we just updated — skip check to be safe

    if expected_version == __version__:
        logger.info(f"Updated successfully to {__version__} — skipping update check")
    else:
        logger.warning(
            f"Update lock version mismatch: expected {expected_version!r}, "
            f"running {__version__!r}. "
            f"Skipping update check this session to prevent restart loop."
        )
    return True  # Either way, skip the update check this session


# Keep old name as an alias so nothing breaks if it's called directly
def cleanup_old_exe():
    """Deprecated: use post_update_startup() instead."""
    post_update_startup()


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
    target_version: str = "",
    on_progress: Optional[Callable[[int, int], None]] = None,
    on_done: Optional[Callable[[bool, str], None]] = None,
):
    """Download the update zip, swap files in place, and relaunch.

    target_version: the version string from the release tag (e.g. "v1.2.0").
                    Written to the update lock before relaunching so the new
                    process can verify the update succeeded.
    on_progress(downloaded_bytes, total_bytes) — called during download.
    on_done(success, message) — called when finished or on error.

    This runs in a background thread.
    """
    def _worker():
        try:
            _do_download_and_install(download_url, target_version, on_progress)
            if on_done:
                on_done(True, "Update ready — restarting...")
        except Exception as e:
            logger.error(f"Update failed: {e}", exc_info=True)
            if on_done:
                on_done(False, str(e))

    threading.Thread(target=_worker, daemon=True).start()


def _do_download_and_install(
    download_url: str,
    target_version: str = "",
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

        # Write update lock BEFORE touching any files.
        # The new process reads this on startup to know it just updated
        # and skips the update check — preventing infinite restart loops.
        lock = _get_lock_path()
        try:
            # Store the version we're installing (strip leading 'v')
            lock_version = target_version.lstrip("vV") if target_version else "unknown"
            lock.write_text(lock_version, encoding="utf-8")
            logger.info(f"Wrote update lock: expecting {lock_version}")
        except Exception as e:
            logger.warning(f"Could not write update lock: {e}")

        # Rename running exe so we can overwrite it
        logger.info(f"Renaming {exe_name} → {exe_name}.old")
        if old_exe.exists():
            old_exe.unlink()
        os.rename(app_dir / exe_name, old_exe)

        # Copy all new files over the app directory
        logger.info(f"Copying new files from {source_dir} → {app_dir}")
        _copy_tree(source_dir, app_dir)

        # Launch the new exe via a batch script intermediary.
        # We CANNOT launch it directly here — the old process is still running
        # and holds its python3XX.dll locked. If the new build uses a different
        # Python version, PyInstaller's multiprocessing bootstrap will crash with
        # "Module use of pythonNNN.dll conflicts with this version of Python."
        # The batch script waits until this PID disappears (all DLLs released),
        # then starts the new exe cleanly.
        new_exe = app_dir / exe_name
        logger.info(f"Scheduling relaunch via batch script: {new_exe}")
        _relaunch_via_bat(new_exe, app_dir)
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
                # Skip files that are locked (e.g. DLLs in use by the running process).
                # These will be cleaned up by _cleanup_old_python_dlls() on next startup.
                logger.warning(f"Skipped locked file: {target}")
            except Exception as e:
                logger.warning(f"Failed to copy {item.name}: {e}")


def _relaunch_via_bat(new_exe: Path, app_dir: Path):
    """Write a self-deleting batch script that waits for this process to exit,
    then launches the new exe.

    Launching the new exe directly from the old process causes a Python DLL
    conflict when the update crosses Python versions (e.g. 3.11 → 3.14):
    the old process still holds python311.dll locked, and PyInstaller's
    multiprocessing bootstrap in the new exe detects the conflict and crashes.
    Waiting for the old process to fully exit releases all its DLL handles
    so the new process starts with a clean slate.
    """
    pid = os.getpid()
    bat = app_dir / "_lolreview_relaunch.bat"

    # The batch logic:
    # 1. Wait ~1s (ping trick — no sleep command in plain cmd)
    # 2. Check if our PID is still alive via tasklist
    # 3. If still alive, loop; otherwise launch the new exe and self-delete
    script = (
        "@echo off\n"
        ":wait\n"
        "ping -n 2 127.0.0.1 >NUL\n"
        f"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"INFO:\" >NUL\n"
        "if errorlevel 1 goto wait\n"
        f"start \"\" \"{new_exe}\"\n"
        "(goto) 2>NUL & del \"%~f0\"\n"
    )

    try:
        bat.write_text(script, encoding="utf-8")
        subprocess.Popen(
            ["cmd.exe", "/c", str(bat)],
            creationflags=(
                subprocess.DETACHED_PROCESS
                | subprocess.CREATE_NEW_PROCESS_GROUP
                | subprocess.CREATE_NO_WINDOW
            ),
            close_fds=True,
        )
        logger.info(f"Relaunch batch script started (watching PID {pid})")
    except Exception as e:
        logger.error(f"Failed to start relaunch script: {e}")
        # Fall back to direct launch — may crash if Python version changed,
        # but better than silently failing to update at all.
        subprocess.Popen(
            [str(new_exe)],
            creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
            close_fds=True,
        )


def _cleanup_old_python_dlls():
    """Delete Python DLL files from previous Python versions left in the app dir.

    When updating across Python versions (e.g. 3.11 → 3.14) the old DLL
    (python311.dll) cannot be overwritten during the copy phase because the
    running process holds it locked. After the old process exits the new one
    starts, but the stale DLL is still sitting on disk. On the NEXT update
    cycle (or if something re-scans the folder) having two python*.dll files
    present can cause another conflict. Clean them up on startup.
    """
    if not getattr(sys, "frozen", False):
        return

    current_dll = f"python{sys.version_info.major}{sys.version_info.minor}.dll"
    app_dir = Path(sys.executable).parent

    # DLLs may live in the root app dir or in _internal/ (PyInstaller 6+)
    search_dirs = [app_dir, app_dir / "_internal"]
    for search_dir in search_dirs:
        if not search_dir.exists():
            continue
        for dll in search_dir.glob("python*.dll"):
            if dll.name.lower() != current_dll.lower():
                try:
                    dll.unlink()
                    logger.info(f"Removed stale Python DLL: {dll}")
                except Exception as e:
                    logger.warning(f"Could not remove stale DLL {dll.name}: {e}")

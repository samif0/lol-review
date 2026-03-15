"""Auto-update system using GitHub Releases.

On startup the app checks for a newer release. If found, it:
1. Downloads the release zip to a temp file
2. Extracts to a staging temp folder
3. Launches a detached PowerShell script, then exits

The PowerShell script (runs after the old process fully exits):
1. Waits for the old process to die (by PID, not a fixed sleep)
2. Uses robocopy /MIR to mirror the staging dir over the app dir
   — copies all new files AND deletes old files not in the new build
   — this eliminates DLL conflicts (old python3XX.dll gets removed)
3. Writes the update lock file
4. Launches the new exe
5. Cleans up the staging directory

On next launch, the app reads the lock file:
- If the lock exists, we just updated — skip the update check this session
  to prevent infinite update loops (lock is deleted after being read).
- The app also cleans up leftover .old files and stale DLLs.

Why robocopy /MIR instead of in-place _copy_tree:
- Runs AFTER old process exits — no locked files, no skipped DLLs
- Handles the DELETE case — stale python3XX.dll gets removed
- Retries on failure (/R:5 /W:2)
- Built into Windows since Vista

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
from .constants import UPDATE_CHECK_TIMEOUT_S, UPDATE_DOWNLOAD_TIMEOUT_S, DOWNLOAD_CHUNK_SIZE
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

        resp = requests.get(_RELEASES_URL, headers=headers, timeout=UPDATE_CHECK_TIMEOUT_S)
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
    """Download zip, extract to staging, schedule PowerShell swap after exit."""
    headers = _get_auth_headers()
    dl_headers = {**headers, "Accept": "application/octet-stream"}

    logger.info(f"Downloading update from {download_url}")

    # ── Download ─────────────────────────────────────────────
    resp = requests.get(download_url, headers=dl_headers, stream=True, timeout=UPDATE_DOWNLOAD_TIMEOUT_S)
    resp.raise_for_status()

    total = int(resp.headers.get("content-length", 0))
    tmp_zip = Path(tempfile.mktemp(suffix=".zip", prefix="lolreview_update_"))

    downloaded = 0
    with open(tmp_zip, "wb") as f:
        for chunk in resp.iter_content(chunk_size=DOWNLOAD_CHUNK_SIZE):
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

    # ── Schedule swap or copy directly ───────────────────────
    if getattr(sys, "frozen", False):
        app_dir = Path(sys.executable).parent
        exe_name = Path(sys.executable).name
        lock_version = target_version.lstrip("vV") if target_version else "unknown"

        # DO NOT touch any files in the app directory while we're running!
        # The running process holds its exe, python DLLs, and loaded .pyd
        # files locked. Any attempt to copy/overwrite/delete them either
        # fails silently or leaves a corrupt mix of old and new files.
        #
        # Instead, hand everything off to a PowerShell script that runs
        # AFTER this process exits, when nothing is locked. PowerShell
        # uses robocopy /MIR to do a clean mirror from staging → app dir.

        logger.info(f"Scheduling PowerShell swap: {source_dir} → {app_dir}")
        _relaunch_via_powershell(
            app_dir=app_dir,
            exe_name=exe_name,
            staging_dir=source_dir,
            staging_cleanup_dir=tmp_extract,
            lock_version=lock_version,
        )

        # Only clean up the zip. DO NOT delete tmp_extract —
        # PowerShell needs the staging files after this process exits.
        try:
            tmp_zip.unlink()
        except Exception:
            pass

    else:
        # Running from source — just copy files (for dev testing)
        app_dir = Path(__file__).resolve().parent.parent
        logger.info(f"Dev mode: copying new files from {source_dir} → {app_dir}")
        _copy_tree(source_dir, app_dir)
        try:
            tmp_zip.unlink()
        except Exception:
            pass
        try:
            shutil.rmtree(tmp_extract)
        except Exception:
            pass

    logger.info("Update staged — will complete after process exit")


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


def _relaunch_via_powershell(
    app_dir: Path,
    exe_name: str,
    staging_dir: Path,
    staging_cleanup_dir: Path,
    lock_version: str,
):
    """Launch a detached PowerShell that installs the update after this process exits.

    This is the ONLY reliable way to update a PyInstaller app on Windows.
    All file operations happen after the old process has fully exited and
    released every file lock. No DLL conflicts, no partial installs.

    The PowerShell script:
    1. Waits for this process to exit (by PID, deterministic)
    2. robocopy /MIR staging → app dir (copies new, deletes old)
    3. Writes the update lock file (AFTER robocopy so /MIR doesn't delete it)
    4. Launches the new exe
    5. Cleans up the staging directory

    Base64-encoding the command avoids quoting issues for paths with spaces.
    """
    import base64

    pid = os.getpid()
    new_exe_str = str(app_dir / exe_name)
    lock_path_str = str(app_dir / ".update_pending")
    log_path_str = str(app_dir / ".update_log.txt")

    parts = [
        # Log file for debugging update issues
        f"$log = '{log_path_str}'",
        f"\"$(Get-Date) - Update swap starting, waiting for PID {pid}\" | Out-File $log",

        # Wait for the old process to fully exit and release all file handles
        f"try {{ Wait-Process -Id {pid} -Timeout 30; "
        f"\"$(Get-Date) - Process exited cleanly\" | Out-File $log -Append "
        f"}} catch {{ \"$(Get-Date) - Wait timed out or process already gone\" | Out-File $log -Append }}",
        "Start-Sleep -Seconds 2",

        # Mirror the staging directory over the app directory.
        # /MIR copies all new files AND deletes files not in source
        # (this is what kills stale python3XX.dll — the root cause of all crashes).
        # /R:5 /W:2 = retry 5 times, 2 seconds between retries.
        f"\"$(Get-Date) - Running robocopy '{staging_dir}' -> '{app_dir}'\" | Out-File $log -Append",
        f"& robocopy '{staging_dir}' '{app_dir}' /MIR /R:5 /W:2 /NFL /NDL /NJH /NJS /NC /NS",
        f"\"$(Get-Date) - Robocopy exit code: $LASTEXITCODE\" | Out-File $log -Append",

        # robocopy exit codes: 0-7 = success, 8+ = error.
        # Only launch the new exe if robocopy succeeded.
        f"if ($LASTEXITCODE -lt 8) {{ "
        f"Set-Content -Path '{lock_path_str}' -Value '{lock_version}' -Encoding UTF8; "
        f"\"$(Get-Date) - Launching {exe_name}\" | Out-File $log -Append; "
        f"Start-Process -FilePath '{new_exe_str}' "
        f"}} else {{ \"$(Get-Date) - FAILED: robocopy exit code $LASTEXITCODE, not launching\" | Out-File $log -Append }}",

        # Clean up staging directory (wait a moment for the new exe to start)
        "Start-Sleep -Seconds 3",
        f"Remove-Item -Path '{staging_cleanup_dir}' -Recurse -Force",
        f"\"$(Get-Date) - Update swap complete\" | Out-File $log -Append",
    ]

    ps_script = "; ".join(parts)
    encoded = base64.b64encode(ps_script.encode("utf-16-le")).decode("ascii")

    try:
        subprocess.Popen(
            [
                "powershell.exe",
                "-NoProfile",
                "-NonInteractive",
                "-WindowStyle", "Hidden",
                "-EncodedCommand", encoded,
            ],
            creationflags=(
                subprocess.DETACHED_PROCESS
                | subprocess.CREATE_NEW_PROCESS_GROUP
                | subprocess.CREATE_NO_WINDOW
            ),
            close_fds=True,
        )
        logger.info(
            f"PowerShell swap scheduled (PID {pid}): "
            f"robocopy '{staging_dir}' → '{app_dir}', then launch {exe_name}"
        )
    except Exception as e:
        logger.error(f"PowerShell launch failed, falling back to legacy install: {e}")
        _legacy_fallback_install(app_dir, exe_name, staging_dir, lock_version)


def _legacy_fallback_install(
    app_dir: Path,
    exe_name: str,
    staging_dir: Path,
    lock_version: str,
):
    """Fallback installer when PowerShell is unavailable.

    Uses the old _copy_tree approach: rename exe, copy files, direct launch.
    Works for same-Python-version updates but may fail for cross-version
    updates due to DLL conflicts. Better than silently failing to update.
    """
    old_exe = app_dir / (exe_name + ".old")
    new_exe = app_dir / exe_name

    # Write update lock
    try:
        lock = _get_lock_path()
        lock.write_text(lock_version, encoding="utf-8")
    except Exception as e:
        logger.warning(f"Could not write update lock: {e}")

    # Rename running exe so we can copy the new one
    try:
        if old_exe.exists():
            old_exe.unlink()
        os.rename(new_exe, old_exe)
    except Exception as e:
        logger.error(f"Could not rename exe: {e}")

    # Copy files (may skip locked ones — degraded but better than nothing)
    _copy_tree(staging_dir, app_dir)

    # Direct launch
    try:
        subprocess.Popen(
            [str(new_exe)],
            creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
            close_fds=True,
        )
    except Exception as e:
        logger.error(f"Direct launch failed: {e}")


def _cleanup_old_python_dlls():
    """Delete stale Python DLL files left behind by cross-version updates.

    Third layer of defence (belt and suspenders):
    - Layer 1: _pre_copy_dll_cleanup renames stale DLLs to .old before copy
    - Layer 2: PowerShell deletes stale DLLs after old process exits
    - Layer 3: This function cleans up anything that slipped through

    Uses python3[0-9]*.dll pattern to avoid accidentally deleting python3.dll
    (the stable ABI forwarder DLL that PyInstaller also bundles).
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
        # Remove version-specific DLLs from other Python versions
        for dll in search_dir.glob("python3[0-9]*.dll"):
            if dll.name.lower() != current_dll.lower():
                try:
                    dll.unlink()
                    logger.info(f"Removed stale Python DLL: {dll}")
                except Exception as e:
                    logger.warning(f"Could not remove stale DLL {dll.name}: {e}")
        # Remove .dll.old leftovers from _pre_copy_dll_cleanup
        for old_dll in search_dir.glob("*.dll.old"):
            try:
                old_dll.unlink()
                logger.info(f"Removed old DLL: {old_dll}")
            except Exception as e:
                logger.warning(f"Could not remove {old_dll.name}: {e}")

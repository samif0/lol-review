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
    """Download zip, extract, rename running exe, copy new files over."""
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

        # Identify the Python DLL the new build ships so we can clean up stale ones.
        expected_python_dll = _find_expected_python_dll(source_dir)
        if expected_python_dll:
            logger.info(f"New build expects: {expected_python_dll}")

        # CRITICAL: Rename stale Python DLLs BEFORE copying new files.
        # When crossing Python versions (e.g. 3.14→3.11), _copy_tree will ADD
        # the new python311.dll but the old python314.dll stays (locked by the
        # running process, can't be deleted). Two python3XX.dll files in the
        # same directory causes PyInstaller's bootloader to crash BEFORE any
        # Python code runs — so _cleanup_old_python_dlls() on startup can never
        # help. Windows allows renaming locked files on NTFS, so we rename
        # the stale DLL to .old which the bootloader ignores.
        _pre_copy_dll_cleanup(source_dir, app_dir)

        # Copy all new files over the app directory
        logger.info(f"Copying new files from {source_dir} → {app_dir}")
        _copy_tree(source_dir, app_dir)

        # Launch via a PowerShell intermediary that:
        # 1. Waits for this process to fully exit (DLL handles released)
        # 2. Deletes any remaining stale python3XX.dll files
        # 3. Launches the new exe in a clean state
        new_exe = app_dir / exe_name
        logger.info(f"Scheduling relaunch via PowerShell: {new_exe}")
        _relaunch_via_powershell(new_exe, expected_python_dll=expected_python_dll)
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


def _relaunch_via_powershell(
    new_exe: Path,
    expected_python_dll: Optional[str] = None,
):
    """Relaunch the new exe via a detached PowerShell process.

    The script waits for this process to exit (by PID), then:
    1. Deletes any stale python3XX.dll files that don't match the new build
    2. Cleans up .old and .dll.old leftovers
    3. Launches the new exe

    This is the critical second layer of defence against the cross-version
    DLL conflict.  The first layer (_pre_copy_dll_cleanup) renames the stale
    DLL before _copy_tree, but if that rename fails for any reason, this
    PowerShell cleanup catches it after all file locks are released.

    Uses Wait-Process instead of a fixed sleep so timing is deterministic.
    Base64-encoding the command avoids quoting issues for paths with spaces.
    """
    import base64

    pid = os.getpid()
    app_dir_str = str(new_exe.parent)
    internal_dir_str = str(new_exe.parent / "_internal")
    old_exe_str = str(new_exe) + ".old"

    # Build the PowerShell script
    parts = [
        # Wait for the old process to fully exit and release all DLL handles
        "$ErrorActionPreference = 'SilentlyContinue'",
        f"try {{ Wait-Process -Id {pid} -Timeout 30 }} catch {{ }}",
        "Start-Sleep -Seconds 2",
    ]

    # Delete stale Python DLLs — the key fix for cross-version updates
    if expected_python_dll:
        for d in [app_dir_str, internal_dir_str]:
            parts.append(
                f"Get-ChildItem '{d}\\python3[0-9]*.dll' -ErrorAction SilentlyContinue | "
                f"Where-Object {{ $_.Name -ne '{expected_python_dll}' }} | "
                f"Remove-Item -Force -ErrorAction SilentlyContinue"
            )
    else:
        # Don't know the expected DLL — at least clean up .old files
        logger.warning("No expected Python DLL name — skipping targeted DLL cleanup")

    # Clean up rename leftovers (.dll.old from _pre_copy_dll_cleanup)
    for d in [app_dir_str, internal_dir_str]:
        parts.append(
            f"Get-ChildItem '{d}\\*.dll.old' -ErrorAction SilentlyContinue | "
            f"Remove-Item -Force -ErrorAction SilentlyContinue"
        )

    # Clean up old exe
    parts.append(f"Remove-Item '{old_exe_str}' -Force -ErrorAction SilentlyContinue")

    # Launch the new exe
    parts.append(f"Start-Process -FilePath '{new_exe}'")

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
            f"PowerShell relaunch scheduled (waiting on PID {pid}): {new_exe}"
        )
    except Exception as e:
        logger.error(f"PowerShell relaunch failed, falling back to direct launch: {e}")
        # Direct launch may crash if the Python version changed, but it's
        # better than silently not restarting at all.
        subprocess.Popen(
            [str(new_exe)],
            creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
            close_fds=True,
        )


def _find_expected_python_dll(source_dir: Path) -> Optional[str]:
    """Find the python3XX.dll filename that the new build ships.

    Scans both the source root and _internal/ subdirectory since PyInstaller
    may place the DLL in either location depending on the version.
    """
    for subdir in [".", "_internal"]:
        path = source_dir / subdir
        if path.is_dir():
            for dll in path.glob("python3[0-9]*.dll"):
                return dll.name
    return None


def _pre_copy_dll_cleanup(source_dir: Path, app_dir: Path):
    """Rename stale python DLLs BEFORE _copy_tree to prevent version conflicts.

    When crossing Python versions (e.g. 3.14→3.11), _copy_tree will add the
    new python311.dll but cannot delete the old python314.dll (it's locked by
    the still-running process).  Two version-specific DLLs in the same
    directory causes PyInstaller's bootloader to crash with:
        "Module use of pythonNNN.dll conflicts with this version of Python."

    The crash happens in the C bootloader BEFORE Python starts, so no amount
    of Python-level cleanup on startup can help.

    Windows (NTFS) allows *renaming* locked files even though it blocks
    deletion.  By renaming the stale DLL to .dll.old, the bootloader won't
    find it.  PowerShell deletes the .old file after the old process exits.
    """
    for subdir in [".", "_internal"]:
        src_path = source_dir / subdir
        tgt_path = app_dir / subdir
        if not tgt_path.is_dir():
            continue
        # What version-specific DLLs does the new build ship?
        src_dlls: set = set()
        if src_path.is_dir():
            src_dlls = {f.name.lower() for f in src_path.glob("python3[0-9]*.dll")}
        # Rename any target DLLs that the new build does NOT ship
        for dll in list(tgt_path.glob("python3[0-9]*.dll")):
            if dll.name.lower() not in src_dlls:
                renamed = dll.with_suffix(".dll.old")
                try:
                    if renamed.exists():
                        renamed.unlink()
                    dll.rename(renamed)
                    logger.info(f"Renamed stale DLL: {dll.name} → {renamed.name}")
                except Exception as e:
                    # Rename failed — PowerShell layer will handle it after
                    # the old process exits and releases the file lock.
                    logger.warning(
                        f"Could not rename stale DLL {dll.name}: {e} "
                        f"— PowerShell will clean it up after process exit"
                    )


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

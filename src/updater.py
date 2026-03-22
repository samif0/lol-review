"""Auto-updater using GitHub Releases with side-by-side versioned directories.

Layout:
  LoLReview/
    LoLReview.exe        <- native launcher (reads .current on first launch)
    .current             <- "1.5.3"
    .previous            <- "1.5.2"
    app-1.5.2/
    app-1.5.3/
      LoLReview.exe      <- actual PyInstaller app
      _internal/

Update flow:
1. App checks GitHub for newer version
2. If already installed locally (previous restart failed) -> just restart
3. Otherwise: download ZIP, extract to app-{version}/, update .current
4. Restart via _restart.cmd batch file that:
   - Polls until our PID is dead (no fixed timeout, no race)
   - Launches new version exe directly
   - Deletes itself

Startup safety:
- If .current points to a newer installed version, launch it and exit
  (catches the case where the batch file restart failed but update IS installed)
- If .update_pending exists, we just updated -> show banner, skip check
"""

import logging
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import zipfile
from pathlib import Path
from typing import Callable, Optional, Tuple

import requests

from .config import load_github_token, save_github_token  # noqa: F401 — re-export
from .constants import UPDATE_CHECK_TIMEOUT_S, UPDATE_DOWNLOAD_TIMEOUT_S, DOWNLOAD_CHUNK_SIZE
from .version import __version__, GITHUB_REPO

logger = logging.getLogger(__name__)

# Kept for launcher.c compatibility
UPDATE_RESTART_EXIT_CODE = 42

_RELEASES_URL = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"


# ── SxS layout detection ────────────────────────────────────────────


def get_sxs_root() -> Optional[Path]:
    """Return the SxS root directory, or None if not in SxS layout.

    In SxS layout the exe lives at: <root>/app-X.Y.Z/LoLReview.exe
    So root = exe.parent.parent, and root/.current must exist.
    """
    if not getattr(sys, "frozen", False):
        return None
    app_dir = Path(sys.executable).parent
    candidate = app_dir.parent
    if (candidate / ".current").exists():
        return candidate
    return None


def is_sxs_layout() -> bool:
    return get_sxs_root() is not None


def _get_install_root() -> Optional[Path]:
    """Get the directory where app-{version}/ dirs and .current live.

    - SxS layout: returns the SxS root (parent of app-X.Y.Z/).
    - Flat layout: returns the exe's own directory. The first update
      bootstraps SxS by creating app-{version}/ and .current here.
    - Dev mode: returns None (can't install).
    """
    sxs_root = get_sxs_root()
    if sxs_root:
        return sxs_root
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent
    return None


# ── Pointer file helpers ─────────────────────────────────────────────


def _read_pointer(path: Path) -> str:
    try:
        if path.exists():
            return path.read_text(encoding="utf-8").strip()
    except Exception:
        pass
    return ""


def _write_pointer_atomic(path: Path, value: str):
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(value, encoding="utf-8")
    tmp.replace(path)


# ── Version parsing ──────────────────────────────────────────────────


def parse_version(version_str: str) -> Tuple[int, ...]:
    cleaned = version_str.lstrip("vV").strip()
    try:
        return tuple(int(x) for x in cleaned.split("."))
    except (ValueError, AttributeError):
        return (0, 0, 0)


# ── Auth ─────────────────────────────────────────────────────────────


def _get_auth_headers() -> dict:
    headers = {"Accept": "application/vnd.github.v3+json"}
    token = load_github_token()
    if token:
        headers["Authorization"] = f"token {token}"
    return headers


# ── Startup ──────────────────────────────────────────────────────────


def maybe_redirect_to_current_version():
    """If .current points to a NEWER installed version, launch it and exit.

    Checks both SxS layout (exe in app-X.Y.Z/) and flat layout (exe
    alongside .current after a bootstrap update). This catches cases where
    an update was installed but the restart failed.

    Only redirects to NEWER versions. If .current is older (rollback), stay put.

    Call at the start of main() after logging is configured.
    If we redirect, this function does not return.
    """
    install_root = _get_install_root()
    if not install_root:
        return

    current_file = install_root / ".current"
    if not current_file.exists():
        return

    current = _read_pointer(current_file)
    if not current or current == __version__:
        return

    if parse_version(current) <= parse_version(__version__):
        return

    target_exe = install_root / f"app-{current}" / "LoLReview.exe"
    if not target_exe.exists():
        logger.warning(f"Redirect: app-{current}/LoLReview.exe not found, skipping")
        return

    logger.info(f"Redirecting {__version__} -> {current}")
    try:
        subprocess.Popen(
            f'cmd.exe /c start "" "{target_exe}"',
            creationflags=0x08000000,  # CREATE_NO_WINDOW
        )
        os._exit(0)
    except Exception as e:
        logger.error(f"Redirect failed: {e}")
        # Continue running as current version


def post_update_startup() -> bool:
    """Check for .update_pending lock. Returns True if we just updated."""
    sxs_root = get_sxs_root()

    if sxs_root:
        lock = sxs_root / ".update_pending"
    elif getattr(sys, "frozen", False):
        lock = Path(sys.executable).parent / ".update_pending"
    else:
        lock = Path(tempfile.gettempdir()) / "lolreview_update_pending"

    if not lock.exists():
        return False

    try:
        expected = lock.read_text(encoding="utf-8").strip()
        lock.unlink()
    except Exception as e:
        logger.warning(f"Could not read/clear update lock: {e}")
        return True

    if expected == __version__:
        logger.info(f"Updated successfully to {__version__}")
    else:
        logger.warning(f"Update lock mismatch: expected {expected!r}, running {__version__!r}")

    return True


def cleanup_old_exe():
    """Deprecated alias for post_update_startup()."""
    post_update_startup()


# ── Health registration & cleanup ────────────────────────────────────


def register_successful_start():
    sxs_root = get_sxs_root()
    if not sxs_root:
        return
    version_dir = Path(sys.executable).parent
    try:
        (version_dir / ".healthy").write_text(str(int(time.time())), encoding="utf-8")
        logger.info(f"Registered healthy start for {__version__}")
    except Exception as e:
        logger.warning(f"Could not write .healthy marker: {e}")
    cleanup_old_versions()


def cleanup_old_versions():
    sxs_root = get_sxs_root()
    if not sxs_root:
        return
    current = _read_pointer(sxs_root / ".current")
    previous = _read_pointer(sxs_root / ".previous")
    keep = {f"app-{current}", f"app-{previous}"} - {"app-"}
    for entry in sxs_root.iterdir():
        if not entry.is_dir() or not entry.name.startswith("app-") or entry.name in keep:
            continue
        try:
            shutil.rmtree(entry)
            logger.info(f"Cleaned up old version: {entry.name}")
        except Exception as e:
            logger.warning(f"Could not remove {entry.name}: {e}")


# ── Check for update ─────────────────────────────────────────────────


def check_for_update() -> Optional[dict]:
    """Check GitHub for a newer version. Returns info dict or None."""
    try:
        headers = _get_auth_headers()
        resp = requests.get(_RELEASES_URL, headers=headers, timeout=UPDATE_CHECK_TIMEOUT_S)

        if resp.status_code == 404:
            return None
        resp.raise_for_status()
        data = resp.json()

        latest_tag = data.get("tag_name", "")
        if parse_version(latest_tag) <= parse_version(__version__):
            logger.info(f"Up to date ({__version__})")
            return None

        download_url = ""
        for asset in data.get("assets", []):
            if asset["name"].endswith(".zip"):
                download_url = asset["browser_download_url"]
                break

        clean_version = latest_tag.lstrip("vV")

        # Check if this version is already installed locally
        # (previous download succeeded but restart failed)
        already_installed = False
        install_root = _get_install_root()
        if install_root:
            target_exe = install_root / f"app-{clean_version}" / "LoLReview.exe"
            already_installed = target_exe.exists()
            if already_installed:
                logger.info(f"v{clean_version} already installed locally, just needs restart")

        return {
            "version": latest_tag,
            "clean_version": clean_version,
            "download_url": download_url,
            "release_url": data.get("html_url", ""),
            "release_notes": data.get("body", ""),
            "already_installed": already_installed,
        }

    except Exception as e:
        logger.warning(f"Update check failed: {e}")
        return None


def check_for_update_async(callback: Callable[[Optional[dict]], None]):
    threading.Thread(target=lambda: callback(check_for_update()), daemon=True).start()


# ── Download & install ───────────────────────────────────────────────


def download_and_install(
    download_url: str,
    target_version: str = "",
    on_progress: Optional[Callable[[int, int], None]] = None,
    on_done: Optional[Callable[[bool, str], None]] = None,
):
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


def _do_download_and_install(download_url, target_version="", on_progress=None):
    headers = _get_auth_headers()
    dl_headers = {**headers, "Accept": "application/octet-stream"}
    clean_version = target_version.lstrip("vV") if target_version else "unknown"

    # Download
    resp = requests.get(download_url, headers=dl_headers, stream=True,
                        timeout=UPDATE_DOWNLOAD_TIMEOUT_S)
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

    # Extract
    tmp_extract = Path(tempfile.mkdtemp(prefix="lolreview_extract_"))
    with zipfile.ZipFile(tmp_zip, "r") as zf:
        zf.extractall(tmp_extract)

    # Find app source
    source_dir = _find_app_source(tmp_extract, clean_version)
    if not list(source_dir.glob("*.exe")):
        raise RuntimeError(f"No .exe in update package at {source_dir}")

    # Install to app-{version}/ under the install root.
    # Works for both SxS layout and flat layout (bootstraps SxS on first update).
    install_root = _get_install_root()
    if not install_root:
        raise RuntimeError("Cannot determine install location — download the update manually from GitHub")

    _install_sxs(install_root, source_dir, clean_version)

    # Cleanup temp
    try:
        tmp_zip.unlink()
    except Exception:
        pass
    try:
        shutil.rmtree(tmp_extract)
    except Exception:
        pass

    logger.info(f"Update to {clean_version} installed")


def _find_app_source(extract_dir: Path, version: str) -> Path:
    # SxS layout in ZIP: app-{version}/ directory
    sxs_app_dir = extract_dir / f"app-{version}"
    if sxs_app_dir.is_dir():
        return sxs_app_dir
    for entry in extract_dir.iterdir():
        if entry.is_dir() and entry.name.startswith("app-"):
            return entry
    # Flat ZIP: single subfolder or root
    items = list(extract_dir.iterdir())
    if len(items) == 1 and items[0].is_dir():
        return items[0]
    return extract_dir


def _install_sxs(sxs_root: Path, source_dir: Path, version: str):
    target_dir = sxs_root / f"app-{version}"

    if target_dir.exists():
        shutil.rmtree(target_dir)

    shutil.copytree(str(source_dir), str(target_dir))

    if not (target_dir / "LoLReview.exe").exists():
        if not list(target_dir.glob("*.exe")):
            shutil.rmtree(target_dir)
            raise RuntimeError(f"No exe in {target_dir}")

    old_version = _read_pointer(sxs_root / ".current")
    _write_pointer_atomic(sxs_root / ".current", version)
    if old_version:
        _write_pointer_atomic(sxs_root / ".previous", old_version)

    (sxs_root / ".update_pending").write_text(version, encoding="utf-8")
    logger.info(f"SxS install: .current={version}, .previous={old_version}")


# ── Restart via batch file ───────────────────────────────────────────


def set_current_version(version: str):
    """Update .current pointer. Saves old version as .previous."""
    install_root = _get_install_root()
    if not install_root:
        return
    old = _read_pointer(install_root / ".current")
    if old != version:
        _write_pointer_atomic(install_root / ".current", version)
        if old:
            _write_pointer_atomic(install_root / ".previous", old)
    (install_root / ".update_pending").write_text(version, encoding="utf-8")


def restart_into_version(version: str = "") -> bool:
    """Create a .cmd batch file that waits for us to exit, then launches the new version.

    The batch file:
    1. Polls `tasklist` until our PID is gone (no fixed timeout, no race condition)
    2. Launches the new version's exe via `start`
    3. Deletes itself

    Returns True if the batch was launched successfully (caller should os._exit(0)).
    Returns False on failure.
    """
    install_root = _get_install_root()
    if not install_root:
        logger.error("restart_into_version: cannot determine install root")
        return False

    if not version:
        version = _read_pointer(install_root / ".current")
    if not version:
        logger.error("restart_into_version: no version in .current")
        return False

    target_exe = install_root / f"app-{version}" / "LoLReview.exe"
    if not target_exe.exists():
        logger.error(f"restart_into_version: {target_exe} not found")
        return False

    pid = os.getpid()
    restart_cmd = install_root / "_restart.cmd"

    # Batch script that polls until our PID is dead, then launches new version.
    # - tasklist /NH /FI: check if PID still running (/NH = no header)
    #   When the process is gone, tasklist prints a line starting with "INFO:"
    # - set /a retries: timeout counter — bail after ~60 seconds (60 iterations)
    # - ping -n 2: ~1 second delay between checks
    # - start "": launch exe (empty title required by start syntax)
    # - del "%~f0": delete the batch file itself
    script = (
        '@echo off\r\n'
        'set /a retries=0\r\n'
        ':wait\r\n'
        f'tasklist /NH /FI "PID eq {pid}" 2>nul | findstr /B /C:"INFO:" >nul\r\n'
        'if not errorlevel 1 goto launch\r\n'
        'set /a retries+=1\r\n'
        'if %retries% GEQ 60 goto launch\r\n'
        'ping -n 2 127.0.0.1 >nul\r\n'
        'goto wait\r\n'
        ':launch\r\n'
        f'start "" "{target_exe}"\r\n'
        'del "%~f0"\r\n'
    )

    try:
        restart_cmd.write_text(script, encoding="mbcs")
    except Exception as e:
        logger.error(f"Could not write restart script: {e}")
        return False

    try:
        subprocess.Popen(
            f'cmd.exe /c "{restart_cmd}"',
            creationflags=0x08000000,  # CREATE_NO_WINDOW
        )
        logger.info(f"Restart script launched for app-{version}")
        return True
    except Exception as e:
        logger.error(f"Failed to launch restart script: {e}")
        try:
            restart_cmd.unlink()
        except Exception:
            pass
        return False

"""Side-by-side versioned auto-updater using GitHub Releases.

Architecture (Squirrel/Chrome-style):
  LoLReview/
    LoLReview.exe        <- tiny native launcher (never updated)
    .current             <- text file: "1.5.0" (active version)
    .previous            <- text file: "1.4.0" (rollback target)
    app-1.4.0/           <- previous version (kept for rollback)
    app-1.5.0/           <- current version
      LoLReview.exe      <- actual PyInstaller app
      _internal/

Update flow:
1. App checks GitHub Releases for a newer version (unchanged)
2. Downloads ZIP, extracts to a NEW app-{version}/ directory
   — never touches the running version's files
3. Atomically updates .current pointer to the new version
4. Writes old version to .previous (for rollback)
5. Exits with code 42 — the launcher re-reads .current and launches the new version

The launcher (launcher.c):
- Reads .current, launches app-{version}/LoLReview.exe
- Watches the process for 30 seconds
- If it crashes, reads .previous, rolls back, launches the old version
- If it survives 30s, the launcher exits — the app is healthy

Why this is bulletproof:
- No PowerShell. No robocopy. No file locking. No timing windows.
- The new version is installed into a fresh directory while the old one runs.
- If anything goes wrong, the old version is untouched and the launcher rolls back.
- Stale DLLs are never a problem — each version has its own complete directory.
"""

import logging
import os
import shutil
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

# Exit code that tells the launcher to re-read .current and launch the new version.
UPDATE_RESTART_EXIT_CODE = 42

# GitHub API endpoint for latest release
_RELEASES_URL = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"

# How long after startup before we consider this version "healthy" and clean up old ones
HEALTHY_START_DELAY_S = 60


# ── SxS layout detection ────────────────────────────────────────────


def get_sxs_root() -> Optional[Path]:
    """Return the SxS root directory, or None if not running in SxS layout.

    In SxS layout, the exe is at:  <sxs_root>/app-X.Y.Z/LoLReview.exe
    So sxs_root = exe.parent.parent, and it should contain a .current file.
    """
    if not getattr(sys, "frozen", False):
        return None
    app_dir = Path(sys.executable).parent
    candidate = app_dir.parent
    if (candidate / ".current").exists():
        return candidate
    return None


def is_sxs_layout() -> bool:
    """True if we're running inside a side-by-side versioned directory."""
    return get_sxs_root() is not None


# ── Pointer file helpers ─────────────────────────────────────────────


def _read_pointer(path: Path) -> str:
    """Read a pointer file (.current / .previous). Returns empty string on failure."""
    try:
        if path.exists():
            return path.read_text(encoding="utf-8").strip()
    except Exception:
        pass
    return ""


def _write_pointer_atomic(path: Path, value: str):
    """Write a pointer file atomically (write to .tmp, then rename)."""
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(value, encoding="utf-8")
    tmp.replace(path)


# ── Auth ─────────────────────────────────────────────────────────────


def _get_auth_headers() -> dict:
    """Build request headers, with auth token if available."""
    headers = {"Accept": "application/vnd.github.v3+json"}
    token = load_github_token()
    if token:
        headers["Authorization"] = f"token {token}"
    return headers


# ── Version parsing ──────────────────────────────────────────────────


def parse_version(version_str: str) -> Tuple[int, ...]:
    """Parse a version string like '1.2.3' or 'v1.2.3' into a comparable tuple."""
    cleaned = version_str.lstrip("vV").strip()
    try:
        return tuple(int(x) for x in cleaned.split("."))
    except (ValueError, AttributeError):
        return (0, 0, 0)


# ── Startup ──────────────────────────────────────────────────────────


def post_update_startup() -> bool:
    """Run on every startup. Returns True if we just updated (skip update check).

    In SxS mode, checks the .update_pending lock in the SxS root.
    In legacy mode, checks the lock next to the exe.
    """
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
        logger.warning(f"Update lock version mismatch: expected {expected!r}, running {__version__!r}")

    return True


# Keep old name as alias
def cleanup_old_exe():
    """Deprecated: use post_update_startup() instead."""
    post_update_startup()


# ── Health registration & cleanup ────────────────────────────────────


def register_successful_start():
    """Mark this version as healthy after it has been running for a while.

    Called ~60s after startup from main.py. Writes a .healthy marker and
    cleans up old version directories.
    """
    sxs_root = get_sxs_root()
    if not sxs_root:
        return

    # Write .healthy marker in our version directory
    version_dir = Path(sys.executable).parent
    try:
        (version_dir / ".healthy").write_text(str(int(time.time())), encoding="utf-8")
        logger.info(f"Registered healthy start for {__version__}")
    except Exception as e:
        logger.warning(f"Could not write .healthy marker: {e}")

    # Clean up old versions
    cleanup_old_versions()


def cleanup_old_versions():
    """Remove old version directories, keeping current + previous."""
    sxs_root = get_sxs_root()
    if not sxs_root:
        return

    current = _read_pointer(sxs_root / ".current")
    previous = _read_pointer(sxs_root / ".previous")
    keep = {f"app-{current}", f"app-{previous}"} - {"app-"}

    for entry in sxs_root.iterdir():
        if not entry.is_dir():
            continue
        if not entry.name.startswith("app-"):
            continue
        if entry.name in keep:
            continue
        try:
            shutil.rmtree(entry)
            logger.info(f"Cleaned up old version: {entry.name}")
        except Exception as e:
            logger.warning(f"Could not remove {entry.name}: {e}")


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
    """Download the update and install to a new versioned directory.

    In SxS mode: extracts to app-{version}/, updates .current pointer.
    In legacy mode: falls back to simple file copy (dev testing only).
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
    """Download zip and install to a new versioned directory."""
    headers = _get_auth_headers()
    dl_headers = {**headers, "Accept": "application/octet-stream"}
    clean_version = target_version.lstrip("vV") if target_version else "unknown"

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

    # ── Extract to temp ──────────────────────────────────────
    tmp_extract = Path(tempfile.mkdtemp(prefix="lolreview_extract_"))
    with zipfile.ZipFile(tmp_zip, "r") as zf:
        zf.extractall(tmp_extract)

    logger.info(f"Extracted to {tmp_extract}")

    # ── Find the app files inside the zip ────────────────────
    # ZIP may contain:
    #   (a) Files at root (LoLReview.exe, _internal/, ...)
    #   (b) A single subfolder containing the files
    #   (c) SxS layout (LoLReview.exe launcher + app-X.Y.Z/ + .current)
    #
    # For SxS updates, we want the app-X.Y.Z/ directory contents.
    # For legacy ZIPs, we want all files.

    source_dir = _find_app_source(tmp_extract, clean_version)

    # Validate: the source must contain an exe
    exe_candidates = list(source_dir.glob("*.exe"))
    if not exe_candidates:
        raise RuntimeError(f"No .exe found in update package at {source_dir}")

    logger.info(f"Update source directory: {source_dir} ({len(list(source_dir.iterdir()))} items)")

    # ── Install ──────────────────────────────────────────────
    sxs_root = get_sxs_root()

    if sxs_root and getattr(sys, "frozen", False):
        _install_sxs(sxs_root, source_dir, clean_version)
    elif getattr(sys, "frozen", False):
        # Legacy flat layout — this is the last time this path runs.
        # After this update, the user will have the SxS layout.
        _install_legacy_to_sxs(source_dir, clean_version, tmp_extract)
    else:
        # Dev mode — just copy files
        app_dir = Path(__file__).resolve().parent.parent
        logger.info(f"Dev mode: copying {source_dir} → {app_dir}")
        _copy_tree(source_dir, app_dir)

    # Clean up temp files.
    # For SxS installs, we can clean up immediately since we copied (not moved) the files.
    # For legacy bridge installs, PowerShell cleans up tmp_extract after it's done.
    try:
        tmp_zip.unlink()
    except Exception:
        pass
    if sxs_root:
        try:
            shutil.rmtree(tmp_extract)
        except Exception:
            pass

    logger.info(f"Update to {clean_version} installed successfully")


def _find_app_source(extract_dir: Path, version: str) -> Path:
    """Find the actual app files inside an extracted ZIP.

    Handles both SxS ZIPs (contain app-X.Y.Z/) and legacy flat ZIPs.
    """
    # Check for SxS layout: does the ZIP contain an app-{version}/ directory?
    sxs_app_dir = extract_dir / f"app-{version}"
    if sxs_app_dir.is_dir():
        return sxs_app_dir

    # Check for any app-* directory (version in ZIP might differ slightly)
    for entry in extract_dir.iterdir():
        if entry.is_dir() and entry.name.startswith("app-"):
            return entry

    # Legacy flat ZIP: single subfolder or files at root
    items = list(extract_dir.iterdir())
    if len(items) == 1 and items[0].is_dir():
        return items[0]
    return extract_dir


def _install_sxs(sxs_root: Path, source_dir: Path, version: str):
    """Install update into a new versioned directory under the SxS root."""
    target_dir = sxs_root / f"app-{version}"

    if target_dir.exists():
        logger.warning(f"Version directory already exists, replacing: {target_dir}")
        shutil.rmtree(target_dir)

    # Move the extracted files into the version directory.
    # Use shutil.copytree + rmtree instead of rename, because rename fails
    # across filesystem boundaries (temp dir might be on a different drive).
    logger.info(f"Installing to {target_dir}")
    shutil.copytree(str(source_dir), str(target_dir))

    # Validate the installed exe exists
    installed_exe = target_dir / "LoLReview.exe"
    if not installed_exe.exists():
        # Try to find it
        exes = list(target_dir.glob("*.exe"))
        if not exes:
            shutil.rmtree(target_dir)
            raise RuntimeError(f"Installed version has no exe: {target_dir}")
        logger.warning(f"Expected LoLReview.exe, found: {[e.name for e in exes]}")

    # Update pointers atomically
    old_version = _read_pointer(sxs_root / ".current")
    _write_pointer_atomic(sxs_root / ".current", version)
    if old_version:
        _write_pointer_atomic(sxs_root / ".previous", old_version)

    # Write update lock so the new version knows it just updated
    (sxs_root / ".update_pending").write_text(version, encoding="utf-8")

    logger.info(f"SxS install complete: .current={version}, .previous={old_version}")


def _install_legacy_to_sxs(source_dir: Path, version: str, tmp_extract: Path):
    """Bridge migration: install SxS update over a legacy flat directory.

    The old updater (PowerShell/robocopy) downloads the new ZIP which has
    the SxS layout. This function handles the case where we're still running
    in the old flat layout but the downloaded ZIP is SxS-formatted.

    Since we can't modify our own files while running, we fall back to the
    old PowerShell approach one last time to do the migration.
    """
    import base64
    import subprocess

    app_dir = Path(sys.executable).parent
    exe_name = Path(sys.executable).name
    pid = os.getpid()

    # The source_dir contains the app files. But for the bridge migration,
    # we need to copy the ENTIRE SxS structure (launcher + app-X.Y.Z/ + .current).
    # The tmp_extract root should have this structure.
    #
    # Check if tmp_extract has the SxS layout
    sxs_source = tmp_extract
    items = list(tmp_extract.iterdir())
    if len(items) == 1 and items[0].is_dir():
        sxs_source = items[0]

    log_path = str(app_dir / ".update_log.txt")
    new_launcher = str(app_dir / "LoLReview.exe")

    # Last PowerShell migration — after this, the SxS launcher takes over
    parts = [
        f"$log = '{log_path}'",
        f"\"$(Get-Date) - Bridge migration starting, waiting for PID {pid}\" | Out-File $log",

        f"try {{ Wait-Process -Id {pid} -Timeout 30; "
        f"\"$(Get-Date) - Process exited\" | Out-File $log -Append "
        f"}} catch {{ \"$(Get-Date) - Wait timed out\" | Out-File $log -Append }}",
        "Start-Sleep -Seconds 2",

        f"\"$(Get-Date) - Running robocopy '{sxs_source}' -> '{app_dir}'\" | Out-File $log -Append",
        f"& robocopy '{sxs_source}' '{app_dir}' /MIR /R:5 /W:2 /NFL /NDL /NJH /NJS /NC /NS",
        f"$rc = $LASTEXITCODE",
        f"\"$(Get-Date) - Robocopy exit code: $rc\" | Out-File $log -Append",

        # Always launch the exe (now the launcher)
        f"\"$(Get-Date) - Launching launcher\" | Out-File $log -Append",
        f"Start-Process -FilePath '{new_launcher}'",

        "Start-Sleep -Seconds 3",
        f"Remove-Item -Path '{tmp_extract}' -Recurse -Force -ErrorAction SilentlyContinue",
        f"\"$(Get-Date) - Bridge migration complete\" | Out-File $log -Append",
    ]

    ps_script = "; ".join(parts)
    encoded = base64.b64encode(ps_script.encode("utf-16-le")).decode("ascii")

    try:
        subprocess.Popen(
            [
                "powershell.exe",
                "-NoProfile", "-NonInteractive",
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
        logger.info(f"Bridge migration scheduled: {sxs_source} → {app_dir}")
    except Exception as e:
        logger.error(f"Bridge migration failed to launch PowerShell: {e}")
        raise RuntimeError(f"Could not start bridge migration: {e}")


def _copy_tree(src: Path, dst: Path):
    """Recursively copy src into dst (dev mode only)."""
    for item in src.iterdir():
        target = dst / item.name
        if item.is_dir():
            target.mkdir(exist_ok=True)
            _copy_tree(item, target)
        else:
            try:
                shutil.copy2(item, target)
            except Exception as e:
                logger.warning(f"Failed to copy {item.name}: {e}")

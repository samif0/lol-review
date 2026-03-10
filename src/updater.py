"""Auto-update checker using GitHub Releases.

On startup, the app checks your GitHub repo for a newer release.
If one exists, it shows a notification with a download link.
The user clicks the link, downloads the new zip, and replaces the old folder.

How to publish an update:
1. Bump __version__ in version.py
2. Build with build.py
3. Zip the dist/LoLReview folder
4. Create a GitHub Release with the tag matching the version (e.g. v1.1.0)
5. Attach the zip to the release

Users with the old version will see a notification on next launch.
"""

import logging
import threading
from typing import Callable, Optional, Tuple

import requests

from .version import __version__, GITHUB_REPO

logger = logging.getLogger(__name__)

# GitHub API endpoint for latest release
_RELEASES_URL = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"


def parse_version(version_str: str) -> Tuple[int, ...]:
    """Parse a version string like '1.2.3' or 'v1.2.3' into a comparable tuple."""
    cleaned = version_str.lstrip("vV").strip()
    try:
        return tuple(int(x) for x in cleaned.split("."))
    except (ValueError, AttributeError):
        return (0, 0, 0)


def check_for_update() -> Optional[dict]:
    """Check GitHub Releases for a newer version.

    Returns a dict with update info if available, None otherwise.
    The dict contains: version, download_url, release_url, release_notes
    """
    try:
        logger.info(f"Checking for updates at {_RELEASES_URL}")
        resp = requests.get(
            _RELEASES_URL,
            headers={"Accept": "application/vnd.github.v3+json"},
            timeout=10,
        )
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

        # Find the zip asset download URL
        download_url = ""
        for asset in data.get("assets", []):
            if asset["name"].endswith(".zip"):
                download_url = asset["browser_download_url"]
                break

        # Fall back to the release page if no zip asset
        release_url = data.get("html_url", "")

        return {
            "version": latest_tag,
            "download_url": download_url or release_url,
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
    """Check for updates in a background thread, then call callback with result.

    The callback is called with the update dict or None.
    """
    def _worker():
        result = check_for_update()
        callback(result)

    thread = threading.Thread(target=_worker, daemon=True)
    thread.start()

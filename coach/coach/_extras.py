"""ML-extras pack activation.

The coach sidecar ships in two pieces:

  1. The "core" pack — this file + the rest of the `coach/` package + a
     handful of lightweight deps (fastapi, httpx, numpy, ...). Always
     installed with the app.
  2. The optional "ml" pack — torch, sentence-transformers, hdbscan, and
     their deps. Downloaded on demand the first time the user triggers a
     concept-extraction feature. See `requirements-ml.txt` and
     `packaging/build-ml.ps1`.

At startup, `activate_if_present()` checks a few well-known locations for
the ML pack and appends its site-packages directory to `sys.path` if
found. Concept endpoints in `main.py` can then `import sentence_transformers`
lazily and get an ImportError only if the pack really isn't installed.
"""

from __future__ import annotations

import logging
import os
import sys
from pathlib import Path

logger = logging.getLogger(__name__)

# Relative to the ML pack root (as extracted from coach-ml-<ver>.zip).
_ML_SITE_PACKAGES_SUBDIR = "site-packages"


def _candidate_roots() -> list[Path]:
    """Locations we'll probe for an ML pack, in priority order.

    1. `COACH_ML_DIR` env var — explicit override, useful in tests and
       for users who put the pack in a non-default location.
    2. `%LOCALAPPDATA%\\LoLReview\\data\\coach\\ml\\` — where
       `CoachMlExtrasInstallerService` (PR 4) unpacks the zip.
    3. `<core-pack-root>/../ml/` — sibling dir if a developer extracts
       both packs next to each other for local testing.
    """
    roots: list[Path] = []

    env = os.environ.get("COACH_ML_DIR")
    if env:
        roots.append(Path(env))

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        roots.append(Path(local_app_data) / "LoLReview" / "data" / "coach" / "ml")

    # When running from a built core pack, sys.executable points at
    # <pack-root>/runtime/python.exe. A sibling "ml" dir next to "runtime"
    # is the convention for local dual-extract testing.
    try:
        exe = Path(sys.executable).resolve()
        pack_root = exe.parent.parent
        roots.append(pack_root / "ml")
    except Exception:
        pass

    return roots


def _resolve_site_packages(root: Path) -> Path | None:
    site = root / _ML_SITE_PACKAGES_SUBDIR
    if site.is_dir():
        return site
    return None


def activate_if_present() -> Path | None:
    """Append the ML pack's site-packages to sys.path if we can find it.

    Returns the activated path on success, or None if no pack is present.
    Idempotent — safe to call multiple times.
    """
    for root in _candidate_roots():
        site = _resolve_site_packages(root)
        if site is None:
            continue

        site_str = str(site)
        if site_str in sys.path:
            logger.debug("coach-ml already on sys.path: %s", site_str)
            return site

        sys.path.insert(0, site_str)
        logger.info("coach-ml activated from %s", site_str)
        return site

    # No external pack was found, but that's not necessarily fatal —
    # in dev, the deps may already be installed directly in the venv's
    # site-packages. is_available() does the real check.
    if is_available():
        logger.info("coach-ml pack not found, but required libs are already importable")
    else:
        logger.info("coach-ml not found; concept-extraction features will return 501")
    return None


def is_available() -> bool:
    """Cheap check: is sentence_transformers importable?

    Used by endpoint handlers to decide between running the feature and
    returning a 501 with a helpful message. Doesn't actually import —
    just looks at the import machinery — so it's safe to call on every
    request.
    """
    import importlib.util

    return importlib.util.find_spec("sentence_transformers") is not None

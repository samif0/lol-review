"""Bootstrap CI and drift detection for per-feature ranking (Phase 3 task 4)."""

from __future__ import annotations

import logging
from typing import Sequence

import numpy as np
from scipy import stats

logger = logging.getLogger(__name__)


def spearman_rho(x: Sequence[float], y: Sequence[float]) -> float:
    """Spearman rank correlation. Returns 0.0 if input is constant or too short."""
    if len(x) < 3 or len(y) < 3:
        return 0.0
    try:
        rho, _ = stats.spearmanr(x, y, nan_policy="omit")
        if np.isnan(rho):
            return 0.0
        return float(rho)
    except Exception:
        return 0.0


def partial_spearman(
    x: Sequence[float], y: Sequence[float], control: Sequence[float]
) -> float | None:
    """Spearman of x vs y controlling for control.

    Method: rank-regress control against y, take residuals; rank-regress
    control against x, take residuals; Spearman of the residuals.
    """
    if len(x) < 5 or len(y) < 5 or len(control) < 5:
        return None
    try:
        x_ranks = stats.rankdata(x)
        y_ranks = stats.rankdata(y)
        c_ranks = stats.rankdata(control)

        # Linear regression of ranks: residuals after controlling.
        slope_xc, intercept_xc, *_ = stats.linregress(c_ranks, x_ranks)
        slope_yc, intercept_yc, *_ = stats.linregress(c_ranks, y_ranks)

        x_resid = x_ranks - (slope_xc * c_ranks + intercept_xc)
        y_resid = y_ranks - (slope_yc * c_ranks + intercept_yc)

        rho, _ = stats.spearmanr(x_resid, y_resid, nan_policy="omit")
        if np.isnan(rho):
            return None
        return float(rho)
    except Exception:
        return None


def bootstrap_ci(
    x: Sequence[float],
    y: Sequence[float],
    n_resamples: int = 1000,
    rng_seed: int = 42,
) -> tuple[float, float]:
    """2.5/97.5 percentile bootstrap CI for Spearman ρ."""
    if len(x) < 5:
        return (-1.0, 1.0)
    rng = np.random.default_rng(rng_seed)
    x_arr = np.asarray(list(x))
    y_arr = np.asarray(list(y))
    n = len(x_arr)

    rhos: list[float] = []
    for _ in range(n_resamples):
        idx = rng.integers(0, n, size=n)
        try:
            rho, _ = stats.spearmanr(x_arr[idx], y_arr[idx], nan_policy="omit")
            if not np.isnan(rho):
                rhos.append(float(rho))
        except Exception:
            continue

    if not rhos:
        return (-1.0, 1.0)
    lo = float(np.percentile(rhos, 2.5))
    hi = float(np.percentile(rhos, 97.5))
    return (lo, hi)


def drift_flag(
    x: Sequence[float], y: Sequence[float], threshold: float = 0.2
) -> bool:
    """True if ρ on first half differs from ρ on second half by more than threshold."""
    n = len(x)
    if n < 10:
        return False
    half = n // 2
    first_rho = spearman_rho(list(x)[:half], list(y)[:half])
    second_rho = spearman_rho(list(x)[half:], list(y)[half:])
    return abs(first_rho - second_rho) > threshold

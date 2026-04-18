"""Rank features by predictive power for the user's win outcome.

Plan §7 Phase 3 tasks 3, 5, 6.
"""

from __future__ import annotations

import logging
from collections import defaultdict
from typing import Any

from coach.db import read_core, write_coach
from coach.schemas import SignalRankingEntry, SignalRankingResponse
from coach.signals.stability import (
    bootstrap_ci,
    drift_flag,
    partial_spearman,
    spearman_rho,
)

logger = logging.getLogger(__name__)

MIN_SAMPLE_SIZE = 20
MENTAL_CONTROL_FEATURE = "mental_rating_going_in"


def rerank() -> dict[str, Any]:
    """Recompute the full ranking from current feature_values + games."""

    with read_core() as conn:
        games = {
            int(r["id"]): 1 if r["win"] else 0
            for r in conn.execute("SELECT id, win FROM games WHERE win IS NOT NULL").fetchall()
        }

        features_by_name: dict[str, list[tuple[int, float]]] = defaultdict(list)
        rows = conn.execute(
            "SELECT game_id, feature_name, value FROM feature_values WHERE value IS NOT NULL"
        ).fetchall()
        for r in rows:
            features_by_name[r["feature_name"]].append((int(r["game_id"]), float(r["value"])))

    # Mental-rating control series, aligned to game_ids.
    mental_by_game = dict(features_by_name.get(MENTAL_CONTROL_FEATURE, []))

    ranking_rows: list[dict[str, Any]] = []

    for feature_name, pairs in features_by_name.items():
        if len(pairs) < MIN_SAMPLE_SIZE:
            continue

        xs: list[float] = []
        ys: list[int] = []
        mental: list[float] = []
        for game_id, value in pairs:
            if game_id not in games:
                continue
            xs.append(value)
            ys.append(games[game_id])
            m = mental_by_game.get(game_id)
            mental.append(float(m) if m is not None else 5.0)

        if len(xs) < MIN_SAMPLE_SIZE:
            continue

        rho = spearman_rho(xs, ys)
        partial = None
        if feature_name != MENTAL_CONTROL_FEATURE:
            partial = partial_spearman(xs, ys, mental)

        ci_lo, ci_hi = bootstrap_ci(xs, ys)
        stable = not (ci_lo <= 0 <= ci_hi)
        drift = drift_flag(xs, ys)

        win_mean = _mean([v for v, w in zip(xs, ys) if w == 1])
        loss_mean = _mean([v for v, w in zip(xs, ys) if w == 0])

        ranking_rows.append(
            {
                "feature_name": feature_name,
                "spearman_rho": rho,
                "partial_rho_mental_controlled": partial,
                "ci_low": ci_lo,
                "ci_high": ci_hi,
                "sample_size": len(xs),
                "stable": stable,
                "drift_flag": drift,
                "user_baseline_win_avg": win_mean,
                "user_baseline_loss_avg": loss_mean,
            }
        )

    # Order by |partial_rho| if available else |rho|, filter to stable.
    def _sort_key(row: dict[str, Any]) -> float:
        primary = row["partial_rho_mental_controlled"]
        if primary is None:
            primary = row["spearman_rho"]
        return -abs(primary or 0.0)

    ranking_rows.sort(key=_sort_key)
    for rank, row in enumerate(ranking_rows, start=1):
        row["rank"] = rank

    # Persist (replacement semantics).
    with write_coach() as conn:
        # Clear existing rows; we rewrite the whole ranking each pass.
        conn.execute("DELETE FROM user_signal_ranking")
        conn.executemany(
            """
            INSERT INTO user_signal_ranking
                (feature_name, spearman_rho, partial_rho_mental_controlled,
                 ci_low, ci_high, sample_size, stable, drift_flag,
                 user_baseline_win_avg, user_baseline_loss_avg, rank, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, strftime('%s','now'))
            """,
            [
                (
                    r["feature_name"],
                    r["spearman_rho"],
                    r["partial_rho_mental_controlled"],
                    r["ci_low"],
                    r["ci_high"],
                    r["sample_size"],
                    1 if r["stable"] else 0,
                    1 if r["drift_flag"] else 0,
                    r["user_baseline_win_avg"],
                    r["user_baseline_loss_avg"],
                    r["rank"],
                )
                for r in ranking_rows
            ],
        )

    return {"ranked": len(ranking_rows), "total_features_considered": len(features_by_name)}


def load_ranking() -> SignalRankingResponse:
    with read_core() as conn:
        rows = conn.execute(
            """
            SELECT feature_name, rank, spearman_rho, partial_rho_mental_controlled,
                   ci_low, ci_high, sample_size, stable, drift_flag,
                   user_baseline_win_avg, user_baseline_loss_avg, updated_at
            FROM user_signal_ranking
            ORDER BY rank ASC
            """
        ).fetchall()

    entries = [
        SignalRankingEntry(
            feature_name=r["feature_name"],
            rank=r["rank"],
            spearman_rho=r["spearman_rho"],
            partial_rho_mental_controlled=r["partial_rho_mental_controlled"],
            ci_low=r["ci_low"],
            ci_high=r["ci_high"],
            sample_size=r["sample_size"],
            stable=bool(r["stable"]),
            drift_flag=bool(r["drift_flag"]),
            user_baseline_win_avg=r["user_baseline_win_avg"],
            user_baseline_loss_avg=r["user_baseline_loss_avg"],
        )
        for r in rows
    ]
    computed_at = rows[0]["updated_at"] if rows else 0
    return SignalRankingResponse(entries=entries, computed_at=computed_at)


def _mean(vals: list[float]) -> float | None:
    if not vals:
        return None
    return sum(vals) / len(vals)

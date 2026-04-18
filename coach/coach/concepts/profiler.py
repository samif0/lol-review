"""Build user_concept_profile from review_concepts.

Plan §7 Phase 2 task 5.
"""

from __future__ import annotations

import logging
import math
import time
from collections import defaultdict
from typing import Any

from coach.db import read_core, write_coach
from coach.schemas import ConceptProfileEntry, ConceptProfileResponse

logger = logging.getLogger(__name__)

HALF_LIFE_DAYS = 30.0
COMPOSITE_RECENCY_WEIGHT = 0.4
COMPOSITE_WINCORR_WEIGHT = 0.3
COMPOSITE_POLARITY_WEIGHT = 0.3


def reprofile() -> dict[str, Any]:
    """Rebuild user_concept_profile from current review_concepts + game outcomes."""

    now_ts = int(time.time())
    half_life_s = HALF_LIFE_DAYS * 86400.0

    with read_core() as conn:
        # Pull reviewed concepts with their game win outcome, if known.
        rows = conn.execute(
            """
            SELECT rc.id, rc.concept_canonical, rc.polarity, rc.source_field,
                   rc.created_at, g.win, g.id AS games_id
            FROM review_concepts rc
            LEFT JOIN games g ON g.id = rc.game_id
            WHERE rc.concept_canonical IS NOT NULL AND rc.concept_canonical != ''
            """
        ).fetchall()

    if not rows:
        with write_coach() as conn:
            conn.execute("DELETE FROM user_concept_profile")
        return {"concepts": 0}

    per_concept: dict[str, dict[str, Any]] = defaultdict(
        lambda: {
            "frequency": 0,
            "recency_weighted": 0.0,
            "positive": 0,
            "negative": 0,
            "neutral": 0,
            "last_seen": 0,
            "loss_games": set(),
            "win_games": set(),
            "mistake_or_focus_games": set(),
            "went_well_games": set(),
        }
    )

    for r in rows:
        key = r["concept_canonical"]
        entry = per_concept[key]
        entry["frequency"] += 1

        created = int(r["created_at"] or 0)
        age_s = max(0, now_ts - created)
        decay = math.pow(0.5, age_s / half_life_s) if half_life_s > 0 else 1.0
        entry["recency_weighted"] += decay

        polarity = str(r["polarity"]).lower()
        if polarity == "positive":
            entry["positive"] += 1
        elif polarity == "negative":
            entry["negative"] += 1
        else:
            entry["neutral"] += 1

        entry["last_seen"] = max(entry["last_seen"], created)

        win = r["win"]
        games_id = r["games_id"]
        if games_id is not None and win is not None:
            if int(win) == 1:
                entry["win_games"].add(int(games_id))
            else:
                entry["loss_games"].add(int(games_id))

            source_field = (r["source_field"] or "").lower()
            if source_field in ("mistakes", "focus_next", "spotted_problems"):
                entry["mistake_or_focus_games"].add(int(games_id))
            elif source_field in ("went_well",):
                entry["went_well_games"].add(int(games_id))

    # Win correlations and composite rank.
    profile_rows: list[dict[str, Any]] = []
    for concept, entry in per_concept.items():
        win_corr = _win_correlation(entry)
        polarity_signal = (entry["negative"] - entry["positive"]) / max(1, entry["frequency"])
        # Compose: 0.4 * recency-weighted (normalized by freq) + 0.3 * |win_corr| + 0.3 * polarity
        recency_weighted_ratio = entry["recency_weighted"] / max(1, entry["frequency"])
        composite = (
            COMPOSITE_RECENCY_WEIGHT * recency_weighted_ratio
            + COMPOSITE_WINCORR_WEIGHT * abs(win_corr or 0.0)
            + COMPOSITE_POLARITY_WEIGHT * polarity_signal
        )

        profile_rows.append(
            {
                "concept_canonical": concept,
                "frequency": entry["frequency"],
                "recency_weighted_frequency": entry["recency_weighted"],
                "positive_count": entry["positive"],
                "negative_count": entry["negative"],
                "neutral_count": entry["neutral"],
                "win_correlation": win_corr,
                "last_seen_at": entry["last_seen"],
                "_composite": composite,
            }
        )

    profile_rows.sort(key=lambda r: r["_composite"], reverse=True)
    for rank, r in enumerate(profile_rows, start=1):
        r["rank"] = rank

    with write_coach() as conn:
        conn.execute("DELETE FROM user_concept_profile")
        conn.executemany(
            """
            INSERT INTO user_concept_profile
                (concept_canonical, frequency, recency_weighted_frequency,
                 positive_count, negative_count, neutral_count,
                 win_correlation, last_seen_at, rank, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, strftime('%s','now'))
            """,
            [
                (
                    r["concept_canonical"],
                    r["frequency"],
                    r["recency_weighted_frequency"],
                    r["positive_count"],
                    r["negative_count"],
                    r["neutral_count"],
                    r["win_correlation"],
                    r["last_seen_at"],
                    r["rank"],
                )
                for r in profile_rows
            ],
        )

    return {"concepts": len(profile_rows)}


def load_profile() -> ConceptProfileResponse:
    with read_core() as conn:
        rows = conn.execute(
            """
            SELECT concept_canonical, frequency, recency_weighted_frequency,
                   positive_count, negative_count, neutral_count,
                   win_correlation, last_seen_at, rank
            FROM user_concept_profile
            ORDER BY rank ASC
            """
        ).fetchall()

    entries = [
        ConceptProfileEntry(
            concept_canonical=r["concept_canonical"],
            frequency=r["frequency"],
            recency_weighted_frequency=r["recency_weighted_frequency"],
            positive_count=r["positive_count"],
            negative_count=r["negative_count"],
            neutral_count=r["neutral_count"],
            win_correlation=r["win_correlation"],
            rank=r["rank"],
            last_seen_at=r["last_seen_at"],
        )
        for r in rows
    ]
    return ConceptProfileResponse(entries=entries, total=len(entries))


def _win_correlation(entry: dict[str, Any]) -> float | None:
    """Report max-magnitude correlation between concept occurrence in
    mistakes/focus_next and losses vs occurrence in went_well and wins.

    Plan §7 Phase 2 task 5. Spearman with a binary concept-presence signal is
    equivalent to a point-biserial; we approximate by the relative frequency.
    """
    mf_games = entry["mistake_or_focus_games"]
    ww_games = entry["went_well_games"]
    losses = entry["loss_games"]
    wins = entry["win_games"]

    total = len(losses) + len(wins)
    if total < 5:
        return None

    def _rate(a: set, b: set) -> float:
        if not b:
            return 0.0
        return len(a & b) / len(b)

    mf_in_losses = _rate(mf_games, losses)
    mf_in_wins = _rate(mf_games, wins)
    ww_in_wins = _rate(ww_games, wins)
    ww_in_losses = _rate(ww_games, losses)

    negative_corr = mf_in_losses - mf_in_wins  # high = concept in losses
    positive_corr = ww_in_wins - ww_in_losses  # high = concept in wins

    return max(negative_corr, positive_corr, key=abs) if (mf_games or ww_games) else None

"""Pythagorean expectation per minute over a match timeline.

P1(t) = S1(t)^alpha / (S1(t)^alpha + S2(t)^alpha)

S1, S2 are team aggregate metrics (gold, XP). Final probability = mean of
gold-derived and XP-derived. Plan §7 Phase 1 task 2.

Data source: derived from per-minute buckets assembled in compactor.py.
"""

from __future__ import annotations


DEFAULT_ALPHA = 1.5


def pythagorean_probability(s1: float, s2: float, alpha: float = DEFAULT_ALPHA) -> float:
    """Compute P1 = S1^alpha / (S1^alpha + S2^alpha). Clamp inputs to > 0."""
    s1c = max(s1, 1.0)
    s2c = max(s2, 1.0)
    num = s1c**alpha
    den = num + s2c**alpha
    if den <= 0:
        return 0.5
    return num / den


def timeline_win_probabilities(
    team_gold: list[float],
    enemy_gold: list[float],
    team_xp: list[float],
    enemy_xp: list[float],
    alpha: float = DEFAULT_ALPHA,
) -> list[float]:
    """Minute-by-minute P(team wins). Length is min of the four input lists."""
    n = min(len(team_gold), len(enemy_gold), len(team_xp), len(enemy_xp))
    probs: list[float] = []
    for t in range(n):
        p_gold = pythagorean_probability(team_gold[t], enemy_gold[t], alpha)
        p_xp = pythagorean_probability(team_xp[t], enemy_xp[t], alpha)
        probs.append((p_gold + p_xp) / 2.0)
    return probs

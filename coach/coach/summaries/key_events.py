"""Key-event selection by momentum impact.

For each event, momentum_impact = |P(t+1) - P(t)|. Sort desc, keep top N.
Plan §7 Phase 1 task 3.
"""

from __future__ import annotations

from typing import Any


def select_key_events(
    events: list[dict[str, Any]],
    win_probabilities: list[float],
    top_n: int = 5,
    source: str = "derived",
) -> list[dict[str, Any]]:
    """Return the top-N events ranked by momentum impact.

    Each event dict should have `timestamp_ms` or `start_ms`, `type`, and
    (optionally) `participants`. Extras are passed through.
    """

    if not events or not win_probabilities:
        return []

    enriched: list[dict[str, Any]] = []
    for ev in events:
        ts_ms = _event_timestamp_ms(ev)
        minute = min(ts_ms // 60_000, len(win_probabilities) - 1)
        next_minute = min(minute + 1, len(win_probabilities) - 1)

        p_before = win_probabilities[minute]
        p_after = win_probabilities[next_minute]
        momentum = abs(p_after - p_before)

        enriched.append(
            {
                **ev,
                "_source": source,
                "timestamp_s": int(ts_ms / 1000),
                "win_prob_before": round(p_before, 4),
                "win_prob_after": round(p_after, 4),
                "momentum_impact": round(momentum, 4),
            }
        )

    enriched.sort(key=lambda e: e["momentum_impact"], reverse=True)
    return enriched[:top_n]


def _event_timestamp_ms(event: dict[str, Any]) -> int:
    """Best-effort timestamp extraction.

    The lol-review schema uses seconds-based columns:
      - game_events.game_time_s
      - derived_event_instances.start_time_s / end_time_s
    Try those first, then fall back to ms / s variants in case of future
    schema changes or foreign payloads.
    """
    # Seconds-based (real schema):
    for key in ("game_time_s", "start_time_s", "timestamp_s", "time_s"):
        val = event.get(key)
        if val is not None:
            try:
                return int(val) * 1000
            except (TypeError, ValueError):
                continue
    # Ms-based fallback (hypothetical / external):
    for key in ("timestamp_ms", "start_ms", "time_ms"):
        val = event.get(key)
        if val is not None:
            try:
                return int(val)
            except (TypeError, ValueError):
                continue
    return 0

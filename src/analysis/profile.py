"""Player profile generation — aggregates all data into a single dict.

This profile serves two purposes:
1. Powers the local suggestion engine now
2. Becomes the feature vector for a future AI coaching model

Designed to be JSON-serializable and rank-agnostic — an AI model
can interpret thresholds relative to rank context it receives separately.
"""

import logging
from collections import Counter

logger = logging.getLogger(__name__)


def generate_player_profile(db) -> dict:
    """Build a comprehensive player profile from all available data.

    Each section is wrapped in try/except so a failure in one area
    doesn't prevent the rest of the profile from being generated.
    """
    profile = {}

    # Overall stats (all-time)
    try:
        profile["overall"] = db.games.get_overall_stats() or {}
    except Exception as e:
        logger.debug(f"Profile: overall stats failed: {e}")
        profile["overall"] = {}

    # Recent stats (last 20 games) — for suggestion thresholds
    try:
        profile["recent"] = db.games.get_recent_stats(limit=20)
    except Exception as e:
        logger.debug(f"Profile: recent stats failed: {e}")
        profile["recent"] = {}

    # Per-champion performance
    try:
        profile["champions"] = db.games.get_champion_stats()
    except Exception as e:
        logger.debug(f"Profile: champion stats failed: {e}")
        profile["champions"] = []

    # Matchup stats
    try:
        profile["matchups"] = db.games.get_matchup_stats()
    except Exception as e:
        logger.debug(f"Profile: matchup stats failed: {e}")
        profile["matchups"] = []

    # Mental state data
    try:
        correlation = db.session_log.get_mental_winrate_correlation()
        mental = {"low_wr": 0, "mid_wr": 0, "high_wr": 0, "avg_rating": 5}
        for bracket in correlation:
            label = bracket.get("bracket", "")
            wr = bracket.get("winrate", 0)
            if "1-3" in label or label == "Low":
                mental["low_wr"] = wr
            elif "4-6" in label or label == "Mid":
                mental["mid_wr"] = wr
            elif "7-10" in label or label == "High":
                mental["high_wr"] = wr
        trend = db.session_log.get_mental_trend(limit=50)
        if trend:
            ratings = [t["mental_rating"] for t in trend if t.get("mental_rating")]
            mental["avg_rating"] = round(sum(ratings) / len(ratings), 1) if ratings else 5
        profile["mental"] = mental
    except Exception as e:
        logger.debug(f"Profile: mental stats failed: {e}")
        profile["mental"] = {}

    # Concept tag frequency
    try:
        profile["concept_tags"] = db.concept_tags.get_tag_frequency(limit=20)
    except Exception as e:
        logger.debug(f"Profile: concept tags failed: {e}")
        profile["concept_tags"] = []

    # Objectives summary
    try:
        all_objs = db.objectives.get_all()
        active = [o for o in all_objs if o["status"] == "active"]
        completed = [o for o in all_objs if o["status"] != "active"]
        avg_games = 0
        if completed:
            counts = [o["game_count"] for o in completed if o["game_count"] > 0]
            avg_games = round(sum(counts) / len(counts), 1) if counts else 0
        profile["objectives"] = {
            "active_count": len(active),
            "completed_count": len(completed),
            "avg_games_to_complete": avg_games,
            "active": [{"title": o["title"], "score": o["score"], "game_count": o["game_count"]} for o in active],
        }
    except Exception as e:
        logger.debug(f"Profile: objectives failed: {e}")
        profile["objectives"] = {}

    # Recent form
    try:
        recent_charts = db.games.get_recent_for_charts(limit=20)
        if recent_charts:
            last_10 = recent_charts[-10:]
            last_20 = recent_charts[-20:]
            l10_wr = round(100 * sum(g["win"] for g in last_10) / len(last_10), 1) if last_10 else 0
            l20_wr = round(100 * sum(g["win"] for g in last_20) / len(last_20), 1) if last_20 else 0
            profile["recent_form"] = {
                "last_10_wr": l10_wr,
                "last_20_wr": l20_wr,
                "win_streak": db.games.get_win_streak(),
            }
        else:
            profile["recent_form"] = {}
    except Exception as e:
        logger.debug(f"Profile: recent form failed: {e}")
        profile["recent_form"] = {}

    # Spotted problems (group similar ones)
    try:
        problems = db.games.get_recent_spotted_problems(limit=50)
        counter = Counter()
        for p in problems:
            text = (p.get("spotted_problems") or "").strip()
            if text:
                counter[text] += 1
        profile["spotted_problems"] = [
            {"text": text, "count": count}
            for text, count in counter.most_common(10)
        ]
    except Exception as e:
        logger.debug(f"Profile: spotted problems failed: {e}")
        profile["spotted_problems"] = []

    # Role stats
    try:
        profile["roles"] = db.games.get_role_stats()
    except Exception as e:
        logger.debug(f"Profile: role stats failed: {e}")
        profile["roles"] = []

    # Duration buckets
    try:
        profile["duration_buckets"] = db.games.get_duration_stats()
    except Exception as e:
        logger.debug(f"Profile: duration stats failed: {e}")
        profile["duration_buckets"] = []

    # Session patterns
    try:
        profile["session_patterns"] = db.session_log.get_session_patterns()
    except Exception as e:
        logger.debug(f"Profile: session patterns failed: {e}")
        profile["session_patterns"] = {}

    return profile

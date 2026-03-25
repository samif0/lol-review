"""Smart objective suggestion engine — rule-based analysis of player patterns.

Consumes a player profile dict (from profile.py) and produces actionable
learning objective suggestions. Fully deterministic, no network calls.
"""

import logging
from typing import Optional

logger = logging.getLogger(__name__)


class SuggestionEngine:
    """Stateless engine that generates objective suggestions from a player profile."""

    def generate(self, profile: dict, limit: int = 3) -> list[dict]:
        """Analyze the profile and return top suggestions sorted by confidence."""
        suggestions = []

        for rule in [
            self._check_vision,
            self._check_cs,
            self._check_deaths,
            self._check_mental_gap,
            self._check_negative_tags,
            self._check_losing_matchups,
            self._check_spotted_problems,
        ]:
            try:
                result = rule(profile)
                if result:
                    suggestions.append(result)
            except Exception as e:
                logger.debug(f"Suggestion rule {rule.__name__} failed: {e}")

        suggestions.sort(key=lambda s: s["confidence"], reverse=True)
        return suggestions[:limit]

    def _check_vision(self, profile: dict) -> Optional[dict]:
        """Suggest vision improvement if avg vision score is low."""
        recent = profile.get("recent", {})
        avg_vision = recent.get("avg_vision", 0)
        if not avg_vision or avg_vision >= 15:
            return None

        return {
            "title": "Improve vision control",
            "skill_area": "Map awareness",
            "type": "primary",
            "completion_criteria": "Average 15+ vision score per game",
            "description": (
                "Your recent vision score averages {:.0f}. "
                "Focus on placing wards at key timings (before objectives, "
                "when pushing, and during roams) and buying control wards."
            ).format(avg_vision),
            "reason": f"Your recent vision score ({avg_vision:.0f}) is below 15",
            "confidence": min(0.9, (15 - avg_vision) / 15),
        }

    def _check_cs(self, profile: dict) -> Optional[dict]:
        """Suggest CS improvement if average is low."""
        recent = profile.get("recent", {})
        avg_cs = recent.get("avg_cs_min", 0)
        if not avg_cs or avg_cs >= 6.0:
            return None

        return {
            "title": "Improve CS per minute",
            "skill_area": "Laning",
            "type": "primary",
            "completion_criteria": "Average 6+ CS per minute",
            "description": (
                "Your recent CS averages {:.1f}/min. Practice last-hitting in "
                "practice tool, focus on not missing uncontested minions, and "
                "catch side waves between fights."
            ).format(avg_cs),
            "reason": f"Your recent CS/min ({avg_cs:.1f}) is below 6.0",
            "confidence": min(0.9, (6.0 - avg_cs) / 6.0),
        }

    def _check_deaths(self, profile: dict) -> Optional[dict]:
        """Suggest death reduction if dying too much."""
        recent = profile.get("recent", {})
        avg_deaths = recent.get("avg_deaths", 0)
        if not avg_deaths or avg_deaths <= 6.0:
            return None

        return {
            "title": "Reduce deaths per game",
            "skill_area": "Positioning & decision-making",
            "type": "primary",
            "completion_criteria": "Average 6 or fewer deaths per game",
            "description": (
                "You're averaging {:.1f} deaths recently. Before each fight, "
                "ask: 'Can I die here? Is it worth?' Track your death reasons "
                "to find the most common ones."
            ).format(avg_deaths),
            "reason": f"Your recent deaths ({avg_deaths:.1f}/game) average above 6",
            "confidence": min(0.9, (avg_deaths - 6.0) / 6.0),
        }

    def _check_mental_gap(self, profile: dict) -> Optional[dict]:
        """Suggest mental management if there's a large winrate gap between mental brackets."""
        mental = profile.get("mental", {})
        low_wr = mental.get("low_wr", 0)
        high_wr = mental.get("high_wr", 0)

        if not low_wr or not high_wr:
            return None

        gap = high_wr - low_wr
        if gap < 15:
            return None

        return {
            "title": "Mental state management",
            "skill_area": "Mental",
            "type": "mental",
            "completion_criteria": "Maintain mental rating 5+ in 80% of games",
            "description": (
                "Your winrate is {:.0f}% when mental is high (7-10) but {:.0f}% when "
                "low (1-3) — a {:.0f}pp gap. Practice recognizing tilt early, "
                "take breaks after tough losses, and use the pre-game mood check."
            ).format(high_wr, low_wr, gap),
            "reason": f"Your winrate drops {gap:.0f}pp when mental is low vs high",
            "confidence": min(0.95, gap / 40),
        }

    def _check_negative_tags(self, profile: dict) -> Optional[dict]:
        """Suggest objectives based on frequently-occurring negative concept tags."""
        tags = profile.get("concept_tags", [])
        for tag in tags:
            if tag.get("polarity") != "negative":
                continue
            pct = tag.get("game_pct", 0)
            if pct < 30:
                continue

            tag_name = tag.get("name", "Unknown issue")
            return {
                "title": f"Address: {tag_name}",
                "skill_area": "Gameplay pattern",
                "type": "primary",
                "completion_criteria": f"Reduce '{tag_name}' tag to under 20% of games",
                "description": (
                    "You've tagged '{tag}' in {pct:.0f}% of your recent games. "
                    "This pattern is worth focusing on — identify the specific "
                    "situations where it happens and develop a plan to avoid them."
                ).format(tag=tag_name, pct=pct),
                "reason": f"'{tag_name}' appears in {pct:.0f}% of games",
                "confidence": min(0.85, pct / 60),
            }

        return None

    def _check_losing_matchups(self, profile: dict) -> Optional[dict]:
        """Suggest matchup-specific objectives for frequently-lost matchups."""
        matchups = profile.get("matchups", [])
        for m in matchups:
            games = m.get("games", 0)
            winrate = m.get("winrate", 50)
            if games < 3 or winrate >= 40:
                continue

            champ = m.get("champion_name", "?")
            enemy = m.get("enemy_laner", "?")
            return {
                "title": f"Improve {champ} vs {enemy}",
                "skill_area": "Matchup knowledge",
                "type": "primary",
                "completion_criteria": f"Win 40%+ of {champ} vs {enemy} games",
                "description": (
                    "You're {wr:.0f}% WR in {g} games as {c} vs {e}. "
                    "Study the matchup — when are your power spikes? "
                    "What abilities to watch for? Write matchup notes after each game."
                ).format(wr=winrate, g=games, c=champ, e=enemy),
                "reason": f"{champ} vs {enemy}: {winrate:.0f}% WR over {games} games",
                "confidence": min(0.8, (40 - winrate) / 40),
            }

        return None

    def _check_spotted_problems(self, profile: dict) -> Optional[dict]:
        """Surface recurring spotted problems as objective suggestions."""
        problems = profile.get("spotted_problems", [])
        for p in problems:
            count = p.get("count", 0)
            if count < 3:
                continue

            text = p.get("text", "")
            return {
                "title": f"Address: {text[:50]}",
                "skill_area": "Self-identified",
                "type": "primary",
                "completion_criteria": f"Resolve: {text[:80]}",
                "description": (
                    "You've noted this problem in {n} reviews: \"{problem}\". "
                    "Since you keep spotting it, making it a focused objective "
                    "could help you systematically improve."
                ).format(n=count, problem=text[:120]),
                "reason": f"Noted in {count} game reviews",
                "confidence": min(0.85, count / 8),
            }

        return None

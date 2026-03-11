"""Collect game events from the League Live Client Data API.

The Live Client Data API runs at https://127.0.0.1:2999 during active games.
It provides real-time event data (kills, deaths, objectives) with timestamps —
no Riot API key required, completely local.

This is the same data source that tools like Ascent use for their VOD timelines.
"""

import logging
import time
from typing import Optional

import requests
import urllib3

from ..database.game_events import (
    EVENT_ASSIST,
    EVENT_BARON,
    EVENT_DEATH,
    EVENT_DRAGON,
    EVENT_FIRST_BLOOD,
    EVENT_HERALD,
    EVENT_INHIBITOR,
    EVENT_KILL,
    EVENT_MULTI_KILL,
    EVENT_TURRET,
)

# The Live Client API also uses HTTPS with a self-signed cert
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

logger = logging.getLogger(__name__)

_BASE_URL = "https://127.0.0.1:2999"
_TIMEOUT = 5


def _get(endpoint: str) -> Optional[dict | list]:
    """Make a GET request to the Live Client Data API."""
    try:
        resp = requests.get(
            f"{_BASE_URL}{endpoint}",
            verify=False,
            timeout=_TIMEOUT,
        )
        resp.raise_for_status()
        return resp.json()
    except Exception:
        return None


def get_active_player_name() -> Optional[str]:
    """Get the local player's summoner name from the live game."""
    data = _get("/liveclientdata/activeplayer")
    if data:
        # Try riotIdGameName first (modern), fall back to summonerName
        return data.get("riotId", data.get("summonerName", ""))
    return None


def is_live_api_available() -> bool:
    """Check if the Live Client Data API is reachable (game is running)."""
    return _get("/liveclientdata/activeplayer") is not None


def fetch_live_events() -> Optional[list[dict]]:
    """Fetch the raw event list from the Live Client Data API.

    Returns the raw Riot event list, or None if the API isn't available.
    Each event has EventID, EventName, EventTime (seconds as float),
    and event-specific fields (KillerName, VictimName, etc.)
    """
    data = _get("/liveclientdata/eventdata")
    if data and isinstance(data, dict):
        return data.get("Events", [])
    return None


class LiveEventCollector:
    """Polls the Live Client Data API during a game to collect events.

    Usage:
        collector = LiveEventCollector()
        collector.start()  # Call when game starts (InProgress phase)
        # ... game plays out, collector polls in background ...
        events = collector.stop()  # Call when game ends, returns parsed events
    """

    def __init__(self, poll_interval: float = 10.0):
        self._poll_interval = poll_interval
        self._running = False
        self._raw_events: list[dict] = []
        self._last_event_id = -1
        self._player_name: Optional[str] = None

    def start(self):
        """Start collecting events. Call from a background thread."""
        self._running = True
        self._raw_events = []
        self._last_event_id = -1
        self._player_name = None
        logger.info("Live event collector started")

        # Wait for the live API to become available (game loading)
        for _ in range(60):  # Up to 5 minutes
            if not self._running:
                return
            if is_live_api_available():
                break
            time.sleep(5)
        else:
            logger.warning("Live Client API never became available")
            return

        # Get the player's name for identifying kills vs deaths
        self._player_name = get_active_player_name()
        logger.info(f"Live API active — player: {self._player_name}")

        # Poll for events until stopped
        while self._running:
            try:
                self._poll()
            except Exception as e:
                logger.debug(f"Live event poll error: {e}")
            time.sleep(self._poll_interval)

    def stop(self) -> list[dict]:
        """Stop collecting and return parsed events in our standard format.

        Does one final poll to catch any last-second events.
        """
        # Final poll to get any remaining events
        try:
            self._poll()
        except Exception:
            pass

        self._running = False

        if not self._raw_events:
            logger.info("No live events collected")
            return []

        events = _parse_live_events(self._raw_events, self._player_name or "")
        logger.info(
            f"Live event collector stopped — {len(events)} events "
            f"from {len(self._raw_events)} raw events"
        )
        return events

    def _poll(self):
        """Fetch new events since last poll."""
        raw = fetch_live_events()
        if raw is None:
            return

        # Only process events we haven't seen yet
        for event in raw:
            eid = event.get("EventID", -1)
            if eid > self._last_event_id:
                self._raw_events.append(event)
                self._last_event_id = eid

    @property
    def player_name(self) -> Optional[str]:
        return self._player_name

    @property
    def event_count(self) -> int:
        return len(self._raw_events)


def _parse_live_events(raw_events: list[dict], player_name: str) -> list[dict]:
    """Convert Live Client Data API events to our standard event format.

    The Live API uses summoner names (not participant IDs), which is
    actually easier to work with.
    """
    events = []
    player_lower = player_name.lower()

    for raw in raw_events:
        event_name = raw.get("EventName", "")
        event_time = raw.get("EventTime", 0.0)
        game_time_s = int(event_time)

        if event_name == "ChampionKill":
            killer = raw.get("KillerName", "")
            victim = raw.get("VictimName", "")
            assisters = raw.get("Assisters", [])

            # Player got a kill
            if killer.lower() == player_lower:
                events.append({
                    "event_type": EVENT_KILL,
                    "game_time_s": game_time_s,
                    "details": {"victim": victim},
                })

            # Player died
            if victim.lower() == player_lower:
                events.append({
                    "event_type": EVENT_DEATH,
                    "game_time_s": game_time_s,
                    "details": {"killer": killer},
                })

            # Player assisted
            if any(a.lower() == player_lower for a in assisters):
                events.append({
                    "event_type": EVENT_ASSIST,
                    "game_time_s": game_time_s,
                    "details": {"killer": killer, "victim": victim},
                })

        elif event_name == "DragonKill":
            events.append({
                "event_type": EVENT_DRAGON,
                "game_time_s": game_time_s,
                "details": {
                    "dragon_type": raw.get("DragonType", ""),
                    "stolen": raw.get("Stolen", False),
                    "killer": raw.get("KillerName", ""),
                },
            })

        elif event_name == "BaronKill":
            events.append({
                "event_type": EVENT_BARON,
                "game_time_s": game_time_s,
                "details": {
                    "stolen": raw.get("Stolen", False),
                    "killer": raw.get("KillerName", ""),
                },
            })

        elif event_name == "HeraldKill":
            events.append({
                "event_type": EVENT_HERALD,
                "game_time_s": game_time_s,
                "details": {"killer": raw.get("KillerName", "")},
            })

        elif event_name == "TurretKilled":
            events.append({
                "event_type": EVENT_TURRET,
                "game_time_s": game_time_s,
                "details": {
                    "killer": raw.get("KillerName", ""),
                    "turret": raw.get("TurretKilled", ""),
                },
            })

        elif event_name == "InhibKilled":
            events.append({
                "event_type": EVENT_INHIBITOR,
                "game_time_s": game_time_s,
                "details": {
                    "killer": raw.get("KillerName", ""),
                    "inhib": raw.get("InhibKilled", ""),
                },
            })

        elif event_name == "Multikill":
            streak = raw.get("KillStreak", 2)
            labels = {2: "Double Kill", 3: "Triple Kill",
                      4: "Quadra Kill", 5: "Penta Kill"}
            # Multikill events fire for the killer
            killer = raw.get("KillerName", "")
            if killer.lower() == player_lower:
                events.append({
                    "event_type": EVENT_MULTI_KILL,
                    "game_time_s": game_time_s,
                    "details": {
                        "count": streak,
                        "label": labels.get(streak, f"{streak}x Kill"),
                    },
                })

        elif event_name == "FirstBlood":
            recipient = raw.get("Recipient", "")
            if recipient.lower() == player_lower:
                events.append({
                    "event_type": EVENT_FIRST_BLOOD,
                    "game_time_s": game_time_s,
                    "details": {},
                })

    return events

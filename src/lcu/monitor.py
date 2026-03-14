"""Game monitor that polls the League client for phase transitions."""

import logging
import threading
import time
from typing import Callable, Optional

from ..constants import GAME_MONITOR_POLL_INTERVAL_S, LIVE_EVENT_POLL_INTERVAL_S, MONITOR_STOP_TIMEOUT_S, EOG_STATS_RETRY_ATTEMPTS
from .client import LCUClient
from .credentials import find_credentials
from .live_events import LiveEventCollector
from .models import GameStats
from .stats import extract_stats_from_eog

logger = logging.getLogger(__name__)


class GameMonitor:
    """Monitors the League client for game phase transitions.

    Polls the gameflow phase and triggers callbacks when:
    - Champ select starts (for pre-game focus window)
    - Game starts loading (to auto-close pre-game window)
    - Game ends (for post-game review)

    Uses polling instead of websocket for reliability — the LCU websocket
    can be finicky across client versions.
    """

    def __init__(
        self,
        on_game_end: Callable[[GameStats], None],
        on_champ_select: Optional[Callable[[], None]] = None,
        on_game_start: Optional[Callable[[], None]] = None,
        on_connect: Optional[Callable[[], None]] = None,
        on_disconnect: Optional[Callable[[], None]] = None,
        poll_interval: float = GAME_MONITOR_POLL_INTERVAL_S,
    ):
        self.on_game_end = on_game_end
        self.on_champ_select = on_champ_select
        self.on_game_start = on_game_start
        self.on_connect = on_connect
        self.on_disconnect = on_disconnect
        self.poll_interval = poll_interval

        self._running = False
        self._client: Optional[LCUClient] = None
        self._last_phase: str = "None"
        self._connected = False
        self._current_game_casual = False
        self._event_collector: Optional[LiveEventCollector] = None
        self._collector_thread: Optional[threading.Thread] = None

    def start(self):
        """Start the monitor loop. Call from a background thread."""
        self._running = True
        logger.info("Game monitor started")

        while self._running:
            try:
                self._tick()
            except Exception as e:
                logger.error(f"Monitor tick error: {e}")

            time.sleep(self.poll_interval)

    def stop(self):
        """Signal the monitor to stop."""
        self._running = False
        logger.info("Game monitor stopped")

    def _tick(self):
        """Single monitoring cycle: ensure connection + check phase."""
        # Try to connect if we don't have a client
        if self._client is None or not self._connected:
            creds = find_credentials()
            if creds is None:
                if self._connected:
                    self._connected = False
                    if self.on_disconnect:
                        self.on_disconnect()
                return

            self._client = LCUClient(creds)
            if self._client.is_connected():
                self._connected = True
                logger.info("Connected to League client")
                if self.on_connect:
                    self.on_connect()
            else:
                self._client = None
                return

        # Check current gameflow phase
        try:
            phase = self._client.get_gameflow_phase()
        except Exception:
            # Client might have closed
            self._connected = False
            self._client = None
            if self.on_disconnect:
                self.on_disconnect()
            return

        # Detect transition into ChampSelect
        if phase == "ChampSelect" and self._last_phase != "ChampSelect":
            # Check if this is a casual queue (ARAM, Arena, etc.)
            queue_id = self._client.get_lobby_queue_id()
            self._current_game_casual = self._is_casual_queue(queue_id)
            mode_label = "casual" if self._current_game_casual else "ranked/normal"
            logger.info(f"Champ select started (queue {queue_id} — {mode_label})")

            # Only show pre-game window for ranked/normal games
            if not self._current_game_casual and self.on_champ_select:
                self.on_champ_select()

        # Detect transition into InProgress (loading screen / game start)
        if phase in ("InProgress", "GameStart") and self._last_phase not in ("InProgress", "GameStart"):
            logger.info("Game loading — closing pre-game window")
            if self.on_game_start:
                self.on_game_start()

            # Start collecting live events (no API key needed)
            if not self._current_game_casual:
                self._start_event_collector()

        # Detect transition into EndOfGame
        if phase == "EndOfGame" and self._last_phase != "EndOfGame":
            if self._current_game_casual:
                logger.info("Casual game ended — skipping")
            else:
                logger.info("Game ended — fetching stats")
                self._handle_game_end()
            self._current_game_casual = False

        self._last_phase = phase

    @staticmethod
    def _is_casual_queue(queue_id: int) -> bool:
        """Check if a queue ID is a casual (non-ranked, non-normal) mode."""
        # Known casual queue IDs
        casual_ids = {
            450,   # ARAM
            1700,  # Arena (Cherry)
            1900,  # URF
            900,   # ARURF
            1010,  # Snow ARURF
            1020,  # One for All
            2000,  # Tutorial 1
            2010,  # Tutorial 2
            2020,  # Tutorial 3
            0,     # Custom / Practice Tool
        }
        return queue_id in casual_ids

    def _start_event_collector(self):
        """Start the live event collector in a background thread."""
        self._stop_event_collector()  # Clean up any previous collector

        self._event_collector = LiveEventCollector(poll_interval=LIVE_EVENT_POLL_INTERVAL_S)
        self._collector_thread = threading.Thread(
            target=self._event_collector.start,
            daemon=True,
        )
        self._collector_thread.start()
        logger.info("Live event collector thread started")

    def _stop_event_collector(self) -> list[dict]:
        """Stop the live event collector and return collected events."""
        events = []
        if self._event_collector:
            events = self._event_collector.stop()
            self._event_collector = None
        if self._collector_thread:
            self._collector_thread.join(timeout=MONITOR_STOP_TIMEOUT_S)
            self._collector_thread = None
        return events

    def _handle_game_end(self):
        """Fetch end-of-game stats and fire the callback."""
        if self._client is None:
            return

        # Stop the live event collector and grab events
        live_events = self._stop_event_collector()
        if live_events:
            logger.info(f"Collected {len(live_events)} live events during game")

        # The EOG data might take a moment to be ready — retry a few times
        for attempt in range(EOG_STATS_RETRY_ATTEMPTS):
            eog_data = self._client.get_end_of_game_stats()
            if eog_data:
                stats = extract_stats_from_eog(eog_data)
                if stats:
                    # Try to get summoner name
                    try:
                        summoner = self._client.get_current_summoner()
                        stats.summoner_name = summoner.get(
                            "displayName",
                            summoner.get("gameName", "Unknown"),
                        )
                    except Exception:
                        pass

                    # Attach live events to the stats object
                    stats.live_events = live_events

                    self.on_game_end(stats)
                    return

            time.sleep(2)

        logger.warning("Could not retrieve end-of-game stats after retries")

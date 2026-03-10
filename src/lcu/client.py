"""HTTP client for the League Client Update API."""

import logging
from typing import Any, Optional

import requests
import urllib3

from .credentials import LCUCredentials

# The LCU uses a self-signed cert — suppress the warnings
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

logger = logging.getLogger(__name__)


class LCUClient:
    """HTTP client for the League Client Update API."""

    def __init__(self, credentials: LCUCredentials):
        self.creds = credentials
        self.session = requests.Session()
        self.session.auth = credentials.auth
        self.session.verify = False  # LCU uses self-signed certs
        self.session.headers.update({"Accept": "application/json"})

    def get(self, endpoint: str) -> Any:
        """Make a GET request to the LCU API."""
        url = f"{self.creds.base_url}{endpoint}"
        resp = self.session.get(url, timeout=10)
        resp.raise_for_status()
        return resp.json()

    def is_connected(self) -> bool:
        """Check if the client is reachable."""
        try:
            self.get("/lol-summoner/v1/current-summoner")
            return True
        except Exception:
            return False

    def get_current_summoner(self) -> dict:
        """Get info about the logged-in summoner."""
        return self.get("/lol-summoner/v1/current-summoner")

    def get_gameflow_phase(self) -> str:
        """Get the current gameflow phase (Lobby, InProgress, EndOfGame, etc.)."""
        try:
            return self.get("/lol-gameflow/v1/gameflow-phase")
        except Exception:
            return "None"

    def get_end_of_game_stats(self) -> Optional[dict]:
        """Get the end-of-game stats block. Only available right after a game."""
        try:
            return self.get("/lol-end-of-game/v1/eog-stats-block")
        except requests.HTTPError as e:
            if e.response.status_code == 404:
                return None
            raise

    def get_lobby_queue_id(self) -> int:
        """Get the queue ID from the current lobby/session.

        Returns -1 if unavailable. Common queue IDs:
        420 = Ranked Solo, 440 = Ranked Flex, 400 = Normal Draft,
        450 = ARAM, 1700 = Arena (Cherry), 1900 = URF
        """
        try:
            # During champ select, the gameflow session has queue info
            session = self.get("/lol-gameflow/v1/session")
            return session.get("gameData", {}).get("queue", {}).get("id", -1)
        except Exception:
            return -1

    def get_match_history(self, begin: int = 0, count: int = 20) -> list[dict]:
        """Get recent match history for the current player."""
        try:
            data = self.get(
                f"/lol-match-history/v3/matchlist/account/"
                f"{{accountId}}?begIndex={begin}&endIndex={begin + count}"
            )
            return data.get("games", {}).get("games", [])
        except Exception:
            return []

    def get_ranked_stats(self) -> Optional[dict]:
        """Get current ranked stats for the player."""
        try:
            return self.get("/lol-ranked/v1/current-ranked-stats")
        except Exception:
            return None

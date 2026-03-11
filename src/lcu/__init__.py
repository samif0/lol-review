"""League Client Update (LCU) API integration.

Connects to the local League of Legends client to detect game endings
and retrieve post-game stats. Also collects live game events via the
Live Client Data API (https://127.0.0.1:2999) for VOD timeline markers.
"""

from .models import GameStats
from .monitor import GameMonitor
from .stats import extract_stats_from_eog

__all__ = ["GameStats", "GameMonitor", "extract_stats_from_eog"]

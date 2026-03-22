"""GUI windows for the LoL Review application."""

from .widgets import ConceptTagSelector, StatCard
from .review import ReviewWindow, ReviewPanel
from .pregame import PreGameWindow, SessionDebriefWindow
from .history import HistoryWindow
from .losses import ReviewLossesWindow
from .session_overlay import SessionRulesOverlay
from .manual_entry import ManualEntryWindow
from .session_logger import SessionLoggerWindow
from .claude_context import generate_and_copy
from .game_review import SessionGameReviewWindow, SessionGameReviewPanel
from .dashboard import DashboardWindow
from .vod_player import VodPlayerWindow, VodPlayerPanel
from .settings import SettingsWindow
from .app_window import AppWindow

__all__ = [
    "ConceptTagSelector", "StatCard",
    "ReviewWindow", "ReviewPanel", "PreGameWindow", "HistoryWindow",
    "ReviewLossesWindow", "SessionRulesOverlay", "ManualEntryWindow",
    "SessionLoggerWindow", "generate_and_copy",
    "SessionGameReviewWindow", "SessionGameReviewPanel",
    "DashboardWindow", "VodPlayerWindow", "VodPlayerPanel",
    "SettingsWindow", "AppWindow",
]

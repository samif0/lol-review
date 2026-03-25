"""Analysis module — player profiling and objective suggestions."""

from .profile import generate_player_profile
from .suggestions import SuggestionEngine

__all__ = ["generate_player_profile", "SuggestionEngine"]

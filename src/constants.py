"""Shared constants and utility functions used across the GUI."""

# Color palette — dark theme inspired by League client
COLORS = {
    # Backgrounds
    "bg_dark": "#0a0a0f",        # Main content bg
    "bg_sidebar": "#0d0d15",     # Sidebar bg
    "bg_card": "#12121a",        # Card bg
    "bg_card_hover": "#16161f",  # Card hover
    "bg_input": "#1a1a24",       # Input bg
    # Borders
    "border": "#1e1e2e",         # Subtle border
    "border_bright": "#2a2a3a",  # Visible border
    # Text
    "text": "#e8e8f0",           # Primary text
    "text_dim": "#7070a0",       # Secondary text
    "text_muted": "#404060",     # Muted text
    # Accents
    "accent_blue": "#0099ff",    # Primary accent
    "accent_blue_dim": "#004c80",# Dim blue
    "accent_gold": "#c89b3c",    # Gold accent
    "accent_purple": "#7c3aed",  # Purple accent
    # Status
    "win_green": "#22c55e",      # Win color
    "win_green_dim": "#14532d",  # Dim win
    "loss_red": "#ef4444",       # Loss color
    "loss_red_dim": "#7f1d1d",   # Dim loss
    # Misc
    "tag_bg": "#1e1e2e",         # Tag background
    "star_active": "#fbbf24",    # Star rating
    "star_inactive": "#2a2a3a",  # Inactive star
    # Sidebar
    "sidebar_active": "#0099ff", # Active nav item accent
    "sidebar_hover": "#14141e",  # Hovered nav item bg
}


def format_duration(seconds: int) -> str:
    """Format game duration as MM:SS."""
    return f"{seconds // 60}:{seconds % 60:02d}"


def format_number(n: int) -> str:
    """Format large numbers with K suffix."""
    if n is None:
        n = 0
    if n >= 1000:
        return f"{n / 1000:.1f}k"
    return str(n)

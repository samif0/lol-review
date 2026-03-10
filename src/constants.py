"""Shared constants and utility functions used across the GUI."""

# Color palette — dark theme inspired by League client
COLORS = {
    "bg_dark": "#0a0a0f",
    "bg_card": "#13131a",
    "bg_input": "#1a1a24",
    "border": "#2a2a3a",
    "text": "#e4e4e8",
    "text_dim": "#8888a0",
    "accent_blue": "#0099ff",
    "accent_gold": "#c89b3c",
    "win_green": "#28c76f",
    "loss_red": "#ea5455",
    "tag_bg": "#1e1e2e",
    "star_active": "#fbbf24",
    "star_inactive": "#3a3a4a",
}


def format_duration(seconds: int) -> str:
    """Format game duration as MM:SS."""
    return f"{seconds // 60}:{seconds % 60:02d}"


def format_number(n: int) -> str:
    """Format large numbers with K suffix."""
    if n >= 1000:
        return f"{n / 1000:.1f}k"
    return str(n)

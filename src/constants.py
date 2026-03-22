"""Shared constants and utility functions used across the GUI."""

# ── Game mode filtering ──────────────────────────────────────────────

# Game modes that don't count as "real" games for session tracking / stats
CASUAL_MODES: frozenset[str] = frozenset({
    "ARAM", "CHERRY", "KIWI", "ULTBOOK", "TUTORIAL", "PRACTICETOOL",
})

# SQL fragment for excluding casual modes in queries
CASUAL_MODE_SQL_FILTER = (
    "AND game_mode NOT IN ("
    + ",".join(f"'{m}'" for m in sorted(CASUAL_MODES))
    + ")"
)

# ── Timing / intervals ───────────────────────────────────────────────

GAME_MONITOR_POLL_INTERVAL_S = 5.0        # How often the LCU monitor polls for game state
LIVE_EVENT_POLL_INTERVAL_S = 10.0         # How often live events are polled during a game
MONITOR_STOP_TIMEOUT_S = 5                # Thread join timeout when stopping the monitor
EOG_STATS_RETRY_ATTEMPTS = 5              # Retries for fetching end-of-game stats

AUTO_REFRESH_INTERVAL_MS = 30_000         # UI auto-refresh interval (session page, overlay)
FLASH_WARNING_INTERVAL_MS = 500           # Loss-streak flash animation interval

STARTUP_VOD_SCAN_DELAY_MS = 3_000         # Delay before scanning for VOD files on startup
VOD_RETRY_DELAY_MS = 90_000               # Retry VOD match after Ascent encoding (~90s)
UPDATE_RESTART_DELAY_MS = 1_500           # Delay before restarting after update install

# ── Game thresholds ──────────────────────────────────────────────────

REMAKE_THRESHOLD_S = 300                  # Games shorter than 5 min are treated as remakes
SESSION_MIN_GAME_DURATION_S = 300         # Minimum game duration for session log rule checks

KDA_EXCELLENT_THRESHOLD = 3.0            # KDA >= this is displayed green
KDA_GOOD_THRESHOLD = 2.0                 # KDA >= this is displayed gold
DAMAGE_DISPLAY_THRESHOLD = 0.02          # Minimum % of total damage to show in breakdown bar

# ── Mental / session ─────────────────────────────────────────────────

MENTAL_RATING_MIN = 1
MENTAL_RATING_MAX = 10
MENTAL_RATING_DEFAULT = 5
MENTAL_RATING_STEPS = MENTAL_RATING_MAX - MENTAL_RATING_MIN  # slider steps

MENTAL_EXCELLENT_THRESHOLD = 8            # Mental >= 8 → green/excellent
MENTAL_DECENT_THRESHOLD = 5              # Mental >= 5 → blue/decent (below = red)
ADHERENCE_STREAK_LOCKED_IN = 3           # Days of adherence to show "locked in"
CONSECUTIVE_LOSS_WARNING = 2             # Consecutive losses before warning flash

# ── Post-loss cooldown (Tice et al. 2001; Verduyn & Lavrijsen 2015) ──
COOLDOWN_DURATION_S = 90                 # Post-loss cooldown suggestion (seconds)
COOLDOWN_BREATHE_INTERVAL_MS = 4000      # Breathing cycle interval (4s in, 4s out)

# ── Pre-game mood (Lieberman et al. 2007 — affect labeling) ───────────
MOOD_LABELS = {1: "Tilted", 2: "Off", 3: "Neutral", 4: "Good", 5: "Locked In"}
MOOD_COLORS = {1: "#ef4444", 2: "#f97316", 3: "#6b7280", 4: "#22c55e", 5: "#10b981"}

# ── Attribution (Weiner 1985; Dweck 2006) ─────────────────────────────
ATTRIBUTION_OPTIONS = [
    ("my_play", "My play"),
    ("team_effort", "Team effort"),
    ("teammates", "Teammates"),
    ("external", "External"),
]

# ── Display limits ────────────────────────────────────────────────────

DEFAULT_RECENT_GAMES_LIMIT = 50          # Default page size for game queries
HISTORY_PAGE_SIZE = 50                   # Paginated history page size
UNREVIEWED_GAMES_DAYS = 3               # Days to look back for unreviewed games
UNREVIEWED_GAMES_DISPLAY_LIMIT = 8      # Max unreviewed games shown on home page

# ── VOD matching ──────────────────────────────────────────────────────

VOD_MATCH_WINDOW_S = 600                 # 10-minute window for matching VODs to games
VOD_MTIME_GRACE_S = 30                  # Grace period for mtime fallback matching

# ── Clip extraction ───────────────────────────────────────────────────

FFMPEG_CRF = 23                          # Quality for re-encode fallback
FFMPEG_CLIP_TIMEOUT_S = 60              # Default timeout for clip extraction
FFMPEG_RE_ENCODE_TIMEOUT_S = 180        # Timeout for re-encode fallback

# ── Updater ───────────────────────────────────────────────────────────

UPDATE_CHECK_TIMEOUT_S = 10
UPDATE_DOWNLOAD_TIMEOUT_S = 60
DOWNLOAD_CHUNK_SIZE = 64 * 1024          # 64 KB chunks

# ── VOD player ────────────────────────────────────────────────────────

VOD_PLAYBACK_SPEEDS = [0.25, 0.5, 1.0, 1.5, 2.0]
VOD_TIME_UPDATE_INTERVAL_MS = 250        # Position display update interval
VOD_ERROR_FLASH_MS = 1_500              # Duration of error flash on invalid input
CLIP_SAVE_FEEDBACK_MS = 3_000           # Duration of "Saved!" feedback on clip save

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

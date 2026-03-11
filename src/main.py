"""LoL Game Review — main entry point.

Runs a system tray icon that monitors the League client in the background.
When a game ends, it pops up a review window with auto-tracked stats.
"""

import logging
import os
import sys
import threading
from pathlib import Path

import customtkinter as ctk
import pystray
from PIL import Image, ImageDraw

from .database import Database, DEFAULT_DB_PATH
from .config import is_ascent_enabled
from .gui import (
    DashboardWindow, HistoryWindow, PreGameWindow, ReviewWindow,
    ReviewLossesWindow, SessionRulesOverlay, ManualEntryWindow,
    SessionLoggerWindow, ClaudeContextWindow, VodPlayerWindow, SettingsWindow,
)
from .lcu import GameMonitor, GameStats
from .updater import check_for_update_async, cleanup_old_exe, download_and_install
from .vod import auto_match_recordings
from .version import __version__

logger = logging.getLogger(__name__)


def create_tray_icon_image(connected: bool = False) -> Image.Image:
    """Create a simple colored icon for the system tray.

    Green circle = connected to League client
    Gray circle = not connected / waiting
    """
    size = 64
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Outer ring
    color = (40, 199, 111) if connected else (100, 100, 120)
    draw.ellipse([4, 4, size - 4, size - 4], fill=color)

    # Inner detail — "GG" feel
    inner_color = (10, 10, 20)
    draw.ellipse([14, 14, size - 14, size - 14], fill=inner_color)

    # Center dot
    center_color = (0, 153, 255) if connected else (60, 60, 80)
    draw.ellipse([24, 24, size - 24, size - 24], fill=center_color)

    return img


class App:
    """Main application coordinating tray, monitor, GUI, and database."""

    def __init__(self):
        self.db = Database()
        self.root: ctk.CTk = None
        self.tray_icon: pystray.Icon = None
        self.monitor: GameMonitor = None
        self._dashboard_window = None
        self._history_window = None
        self._review_window = None
        self._pregame_window = None
        self._review_losses_window = None
        self._session_overlay = None
        self._manual_entry_window = None
        self._session_logger_window = None
        self._claude_context_window = None
        self._vod_player_window = None
        self._settings_window = None
        self._connected = False

        # Set up customtkinter appearance
        ctk.set_appearance_mode("dark")
        ctk.set_default_color_theme("blue")

    def start(self):
        """Start the application: tray icon, game monitor, and tk mainloop."""
        logger.info(f"Starting LoL Game Review v{__version__}")

        # Clean up .old exe from a previous update
        cleanup_old_exe()

        # Create the hidden root window for tkinter event loop
        self.root = ctk.CTk()
        self.root.withdraw()  # Hidden — we only show toplevels
        self.root.title("LoL Game Review")

        # Start the game monitor in a background thread
        self.monitor = GameMonitor(
            on_game_end=self._on_game_end,
            on_champ_select=self._on_champ_select,
            on_game_start=self._on_game_start,
            on_connect=self._on_connect,
            on_disconnect=self._on_disconnect,
            poll_interval=5.0,
        )
        monitor_thread = threading.Thread(target=self.monitor.start, daemon=True)
        monitor_thread.start()

        # Start system tray icon in its own thread
        self._create_tray()
        tray_thread = threading.Thread(target=self.tray_icon.run, daemon=True)
        tray_thread.start()

        logger.info("App started — waiting for League client")

        # Show the startup dashboard
        self._show_dashboard()

        # Check for updates in the background
        check_for_update_async(self._on_update_check_result)

        # Run tk mainloop (blocks until quit)
        self.root.mainloop()

    def _build_tray_menu(self, status: str) -> pystray.Menu:
        """Build the tray icon right-click menu."""
        return pystray.Menu(
            pystray.MenuItem(f"LoL Review v{__version__}", None, enabled=False),
            pystray.MenuItem(f"Status: {status}", None, enabled=False),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Show Dashboard", self._show_dashboard_from_tray),
            pystray.MenuItem("Game History", self._show_history),
            pystray.MenuItem("Review Losses", self._show_review_losses),
            pystray.MenuItem("Session Logger", self._show_session_logger),
            pystray.MenuItem("Claude Context", self._show_claude_context),
            pystray.MenuItem("Manual Entry", self._show_manual_entry),
            pystray.MenuItem("Settings", self._show_settings),
            pystray.MenuItem("Open Data Folder", self._open_data_folder),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Quit", self._quit),
        )

    def _create_tray(self):
        """Set up the system tray icon with menu."""
        self.tray_icon = pystray.Icon(
            "lol-review",
            create_tray_icon_image(connected=False),
            "LoL Game Review",
            self._build_tray_menu("Waiting for League..."),
        )

    def _update_tray_status(self, connected: bool):
        """Update the tray icon appearance and status text."""
        self._connected = connected
        if self.tray_icon:
            self.tray_icon.icon = create_tray_icon_image(connected)
            status = "Connected to League" if connected else "Waiting for League..."
            self.tray_icon.menu = self._build_tray_menu(status)

    def _on_connect(self):
        """Called when the League client is detected."""
        logger.info("Connected to League client")
        self._update_tray_status(True)
        if self.tray_icon:
            self.tray_icon.notify("Connected to League client", "LoL Game Review")

        # Show Session Rules overlay when connected
        self.root.after(0, self._show_session_overlay)

    def _on_disconnect(self):
        """Called when the League client disconnects."""
        logger.info("League client disconnected")
        self._update_tray_status(False)

        # Hide Session Rules overlay when disconnected
        self.root.after(0, self._hide_session_overlay)

    def _on_champ_select(self):
        """Called when champ select begins — show the pre-game focus window."""
        logger.info("Champ select detected — showing pre-game window")
        self.root.after(0, self._show_pregame)

    def _show_pregame(self):
        """Open the pre-game focus window."""
        # Close existing if somehow still open
        if self._pregame_window and self._pregame_window.winfo_exists():
            self._pregame_window.destroy()

        # Pull context from the database
        last_review = self.db.get_last_review_focus()
        recent_games = self.db.get_recent_games(5)
        streak = self.db.get_win_streak()

        self._pregame_window = PreGameWindow(
            last_focus=last_review.get("focus_next", ""),
            last_mistakes=last_review.get("mistakes", ""),
            recent_games=recent_games,
            streak=streak,
            on_dismiss=self._on_pregame_dismiss,
        )

    def _on_pregame_dismiss(self, focus_text: str):
        """Called when the pre-game window is closed (button or auto)."""
        if focus_text:
            logger.info(f"Pre-game focus set: {focus_text}")
        self._pregame_window = None

    def _on_game_start(self):
        """Called when loading screen begins — auto-close the pre-game window."""
        logger.info("Game starting — closing pre-game window")
        self.root.after(0, self._close_pregame)

    def _close_pregame(self):
        """Close the pre-game window if it's still open."""
        if self._pregame_window and self._pregame_window.winfo_exists():
            self._pregame_window.auto_close()
            self._pregame_window = None

    # Game modes that don't count toward your ranked session
    _CASUAL_MODES = {"ARAM", "CHERRY", "ULTBOOK", "TUTORIAL", "PRACTICETOOL"}

    def _on_game_end(self, stats: GameStats):
        """Called when a game ends — save stats, log session, and show review popup."""
        logger.info(
            f"Game ended: {stats.champion_name} "
            f"{'Win' if stats.win else 'Loss'} "
            f"{stats.kills}/{stats.deaths}/{stats.assists} "
            f"({stats.game_mode})"
        )

        # Skip casual modes entirely — no save, no session log, no review popup
        is_casual = stats.game_mode.upper() in self._CASUAL_MODES
        if is_casual:
            logger.info(f"Casual game ({stats.game_mode}) — skipping")
            return

        # Skip remakes (games under 5 minutes)
        is_remake = stats.game_duration < 300
        if is_remake:
            logger.info(f"Remake detected ({stats.game_duration}s) — skipping")
            return

        # Save to database and session log
        self.db.save_game(stats)

        mental = 5
        if self._session_overlay and self._session_overlay.winfo_exists():
            mental = self._session_overlay.get_mental_rating()

        self.db.log_session_game(
            game_id=stats.game_id,
            champion_name=stats.champion_name,
            win=stats.win,
            mental_rating=mental,
        )

        # Save live events collected during the game (no API key needed!)
        if stats.live_events:
            try:
                self.db.save_game_events(stats.game_id, stats.live_events)
                logger.info(
                    f"Saved {len(stats.live_events)} live events for game {stats.game_id}"
                )
            except Exception as e:
                logger.warning(f"Failed to save live events: {e}")

        # Show the review popup on the main thread
        self.root.after(0, lambda: self._show_review(stats))

    def _show_review(self, stats: GameStats):
        """Open the review popup window."""
        # Close existing review window if any
        if self._review_window and self._review_window.winfo_exists():
            self._review_window.destroy()

        tags = self.db.get_all_tags()
        existing = self.db.get_game(stats.game_id)

        # Check for linked VOD
        vod_info = self.db.get_vod(stats.game_id)
        has_vod = vod_info is not None
        bookmarks = self.db.get_bookmarks(stats.game_id) if has_vod else []
        bookmark_count = len(bookmarks)

        # Try to auto-match a recording if Ascent is enabled and no VOD linked yet
        if not has_vod and is_ascent_enabled():
            self._try_auto_match(stats.game_id)
            vod_info = self.db.get_vod(stats.game_id)
            has_vod = vod_info is not None
            if has_vod:
                bookmarks = self.db.get_bookmarks(stats.game_id)
                bookmark_count = len(bookmarks)

        self._review_window = ReviewWindow(
            stats=stats,
            tags=tags,
            existing_review=existing,
            on_save=self._save_review,
            on_open_vod=self._open_vod_player,
            has_vod=has_vod,
            bookmark_count=bookmark_count,
            bookmarks=bookmarks,
        )

    def _save_review(self, review_data: dict):
        """Save review notes to the database."""
        self.db.update_review(**review_data)
        logger.info(f"Review saved for game {review_data['game_id']}")

        if self.tray_icon:
            self.tray_icon.notify("Review saved!", "LoL Game Review")

    def _try_auto_match(self, game_id: int):
        """Attempt to auto-match an Ascent recording to a game."""
        try:
            game = self.db.get_game(game_id)
            if not game:
                return
            recent_games = self.db.get_recent_games(10)
            # Tag which games already have VODs
            for g in recent_games:
                g["has_vod"] = self.db.get_vod(g["game_id"]) is not None
            matches = auto_match_recordings(recent_games)
            for m in matches:
                g = m["game"]
                r = m["recording"]
                self.db.link_vod(
                    g["game_id"], r["path"],
                    file_size=r["size"],
                )
                logger.info(f"Auto-matched VOD: {r['name']} → game {g['game_id']}")
        except Exception as e:
            logger.warning(f"VOD auto-match failed: {e}")

    def _open_vod_player(self, game_id: int):
        """Open the VOD player for a specific game."""
        vod_info = self.db.get_vod(game_id)
        if not vod_info:
            logger.warning(f"No VOD found for game {game_id}")
            return

        game = self.db.get_game(game_id)
        if not game:
            return

        # Close existing player if open
        if self._vod_player_window and self._vod_player_window.winfo_exists():
            self._vod_player_window.destroy()

        bookmarks = self.db.get_bookmarks(game_id)
        tags = self.db.get_all_tags()
        game_events = self.db.get_game_events(game_id)

        self._vod_player_window = VodPlayerWindow(
            game_id=game_id,
            vod_path=vod_info["file_path"],
            game_duration=game.get("game_duration", 0),
            champion_name=game.get("champion_name", "Unknown"),
            bookmarks=bookmarks,
            tags=tags,
            game_events=game_events,
            on_add_bookmark=self._on_add_bookmark,
            on_update_bookmark=self._on_update_bookmark,
            on_delete_bookmark=self._on_delete_bookmark,
        )

    def _on_add_bookmark(self, game_id, game_time_s, note="", tags=None):
        """Save a new bookmark to the database."""
        bm_id = self.db.add_bookmark(game_id, game_time_s, note, tags)
        logger.info(f"Bookmark added: game {game_id} @ {game_time_s}s")
        return bm_id

    def _on_update_bookmark(self, bookmark_id, **kwargs):
        """Update an existing bookmark."""
        self.db.update_bookmark(bookmark_id, **kwargs)

    def _on_delete_bookmark(self, bookmark_id):
        """Delete a bookmark."""
        self.db.delete_bookmark(bookmark_id)
        logger.info(f"Bookmark {bookmark_id} deleted")

    def _show_settings(self, icon=None, item=None):
        """Open the settings window."""
        def _open():
            if self._settings_window and self._settings_window.winfo_exists():
                self._settings_window.lift()
                return
            self._settings_window = SettingsWindow(on_save=self._on_settings_saved)
        self.root.after(0, _open)

    def _on_settings_saved(self):
        """Called when settings are saved — re-scan for VODs."""
        logger.info("Settings saved")
        if is_ascent_enabled():
            # Run auto-match for recent unmatched games
            try:
                recent = self.db.get_recent_games(20)
                for g in recent:
                    g["has_vod"] = self.db.get_vod(g["game_id"]) is not None
                matches = auto_match_recordings(recent)
                for m in matches:
                    g = m["game"]
                    r = m["recording"]
                    self.db.link_vod(g["game_id"], r["path"], file_size=r["size"])
                    logger.info(f"Auto-matched VOD: {r['name']} → game {g['game_id']}")
                if matches:
                    if self.tray_icon:
                        self.tray_icon.notify(
                            f"Matched {len(matches)} recording{'s' if len(matches) != 1 else ''} to games",
                            "LoL Review"
                        )
            except Exception as e:
                logger.warning(f"Post-settings VOD scan failed: {e}")

    def _show_dashboard(self):
        """Show the startup dashboard window."""
        if self._dashboard_window and self._dashboard_window.winfo_exists():
            self._dashboard_window.deiconify()
            self._dashboard_window.lift()
            return

        self._dashboard_window = DashboardWindow(
            db=self.db,
            on_open_history=lambda: self._show_history(),
            on_open_losses=lambda: self._show_review_losses(),
            on_open_session_logger=lambda: self._show_session_logger(),
            on_open_claude_context=lambda: self._show_claude_context(),
            on_open_manual_entry=lambda: self._show_manual_entry(),
            on_open_settings=lambda: self._show_settings(),
            on_minimize=self._on_dashboard_minimized,
            on_open_vod=self._open_vod_player,
        )

    def _show_dashboard_from_tray(self, icon=None, item=None):
        """Re-open the dashboard from the tray menu."""
        self.root.after(0, self._show_dashboard)

    def _on_update_check_result(self, update_info):
        """Called from background thread when update check completes.

        If an update is found, immediately start downloading and installing.
        """
        if update_info is None:
            return

        logger.info(
            f"Update available: {update_info['version']} "
            f"(current: {__version__}) — auto-installing"
        )

        download_url = update_info.get("download_url", "")
        if not download_url:
            logger.warning("Update has no zip asset attached — skipping")
            return

        # Show the "Updating..." banner on the dashboard
        self.root.after(0, lambda: self._show_update_status(
            f"Updating to {update_info['version']}..."
        ))

        # Start the download immediately
        download_and_install(
            download_url,
            on_progress=self._on_update_progress,
            on_done=self._on_update_done,
        )

    def _show_update_status(self, text, color="#a0a0b0"):
        """Show or update the status text on the dashboard banner."""
        if self._dashboard_window and self._dashboard_window.winfo_exists():
            self._dashboard_window.show_update_banner(text, color)

    def _on_update_progress(self, downloaded, total):
        """Called from background thread with download progress."""
        if total > 0:
            pct = int(downloaded / total * 100)
            mb = downloaded / (1024 * 1024)
            mb_total = total / (1024 * 1024)
            text = f"Downloading update... {mb:.1f}/{mb_total:.1f} MB ({pct}%)"
        else:
            mb = downloaded / (1024 * 1024)
            text = f"Downloading update... {mb:.1f} MB"
        try:
            self.root.after(0, lambda t=text: self._show_update_status(t))
        except Exception:
            pass

    def _on_update_done(self, success, message):
        """Called from background thread when download + extract finishes."""
        def _handle():
            if success:
                self._show_update_status("Update installed — restarting...", "#4ade80")
                if self.tray_icon:
                    self.tray_icon.notify("Restarting with new update...", "LoL Game Review")
                # Brief pause so user sees the message, then exit for the batch script
                self.root.after(1500, self._quit)
            else:
                logger.error(f"Auto-update failed: {message}")
                self._show_update_status(f"Update failed: {message}", "#f87171")
        try:
            self.root.after(0, _handle)
        except Exception:
            pass

    def _on_dashboard_minimized(self):
        """Called when the user clicks 'Minimize to Tray'."""
        if self.tray_icon:
            self.tray_icon.notify("Running in background", "LoL Game Review")

    def _show_history(self, icon=None, item=None):
        """Open the game history window."""
        def _open():
            if self._history_window and self._history_window.winfo_exists():
                self._history_window.lift()
                return

            games = self.db.get_recent_games(100)
            overall = self.db.get_overall_stats()
            champ_stats = self.db.get_champion_stats()

            self._history_window = HistoryWindow(
                games=games,
                overall=overall,
                champion_stats=champ_stats,
                db=self.db,
                on_open_vod=self._open_vod_player,
            )

        self.root.after(0, _open)

    def _show_review_losses(self, icon=None, item=None):
        """Open the Review Losses window."""
        def _open():
            if self._review_losses_window and self._review_losses_window.winfo_exists():
                self._review_losses_window.lift()
                return

            self._review_losses_window = ReviewLossesWindow(
                db=self.db,
                on_open_vod=self._open_vod_player,
            )

        self.root.after(0, _open)

    def _show_session_overlay(self):
        """Show the Session Rules overlay."""
        if self._session_overlay and self._session_overlay.winfo_exists():
            self._session_overlay.lift()
            return

        self._session_overlay = SessionRulesOverlay(db=self.db)

    def _hide_session_overlay(self):
        """Hide the Session Rules overlay."""
        if self._session_overlay and self._session_overlay.winfo_exists():
            self._session_overlay.destroy()
            self._session_overlay = None

    def _show_manual_entry(self, icon=None, item=None):
        """Open the Manual Entry window."""
        def _open():
            if self._manual_entry_window and self._manual_entry_window.winfo_exists():
                self._manual_entry_window.lift()
                return

            self._manual_entry_window = ManualEntryWindow(
                db=self.db,
                on_save=self._on_manual_entry_saved,
            )

        self.root.after(0, _open)

    def _on_manual_entry_saved(self):
        """Called when a manual entry is saved."""
        logger.info("Manual game entry saved")
        if self.tray_icon:
            self.tray_icon.notify("Game entry saved!", "LoL Game Review")

        # Refresh session overlay if it's visible
        if self._session_overlay and self._session_overlay.winfo_exists():
            self._session_overlay._refresh_session_data()

        # Refresh session logger if it's visible
        if self._session_logger_window and self._session_logger_window.winfo_exists():
            self._session_logger_window._refresh()

    def _show_session_logger(self, icon=None, item=None):
        """Open the Session Logger window."""
        def _open():
            if self._session_logger_window and self._session_logger_window.winfo_exists():
                self._session_logger_window.lift()
                return

            self._session_logger_window = SessionLoggerWindow(
                db=self.db,
                on_open_vod=self._open_vod_player,
            )

        self.root.after(0, _open)

    def _show_claude_context(self, icon=None, item=None):
        """Open the Claude Context Generator window."""
        def _open():
            if self._claude_context_window and self._claude_context_window.winfo_exists():
                self._claude_context_window.lift()
                return

            self._claude_context_window = ClaudeContextWindow(db=self.db)

        self.root.after(0, _open)

    def _open_data_folder(self, icon=None, item=None):
        """Open the data folder in file explorer."""
        data_dir = self.db.db_path.parent
        os.startfile(str(data_dir))

    def _quit(self, icon=None, item=None):
        """Clean shutdown."""
        logger.info("Shutting down")
        if self.monitor:
            self.monitor.stop()
        if self.tray_icon:
            self.tray_icon.stop()
        if self.db:
            self.db.close()
        if self.root:
            self.root.after(0, self.root.destroy)


def main():
    """Entry point."""
    # Configure logging — store logs alongside the database in AppData
    log_dir = DEFAULT_DB_PATH.parent
    log_dir.mkdir(parents=True, exist_ok=True)

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        handlers=[
            logging.FileHandler(log_dir / "lol_review.log"),
            logging.StreamHandler(sys.stdout),
        ],
    )

    app = App()
    app.start()


if __name__ == "__main__":
    main()

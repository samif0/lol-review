"""Database connection management, path resolution, and migration."""

import logging
import os
import sqlite3
import sys
import time
from pathlib import Path
from typing import Optional

from .schema import (
    CREATE_GAMES_TABLE,
    CREATE_GAME_EVENTS_TABLE,
    CREATE_GAME_EVENTS_INDEX,
    CREATE_MATCHUP_NOTES_TABLE,
    CREATE_OBJECTIVE_PROMPTS_TABLE,
    CREATE_OBJECTIVES_TABLE,
    CREATE_GAME_OBJECTIVES_TABLE,
    CREATE_CONCEPT_TAGS_TABLE,
    CREATE_GAME_CONCEPT_TAGS_TABLE,
    CREATE_DERIVED_EVENT_DEFINITIONS_TABLE,
    CREATE_DERIVED_EVENT_INSTANCES_TABLE,
    CREATE_PERSISTENT_NOTES_TABLE,
    CREATE_PROMPT_ANSWERS_TABLE,
    CREATE_RULES_TABLE,
    CREATE_SESSION_LOG_TABLE,
    CREATE_TAGS_TABLE,
    CREATE_VOD_BOOKMARKS_TABLE,
    CREATE_VOD_FILES_TABLE,
    DEFAULT_CONCEPT_TAGS,
    DEFAULT_DERIVED_EVENTS,
    DEFAULT_TAGS,
    MIGRATE_BOOKMARKS_CLIP_COLUMNS,
    MIGRATE_GAMES_ATTRIBUTION,
    MIGRATE_GAMES_ENEMY_LANER,
    MIGRATE_GAMES_REAPPRAISAL,
    MIGRATE_GAMES_SELF_EFFICACY,
    MIGRATE_GAMES_SPOTTED_PROBLEMS,
    MIGRATE_SESSION_LOG_MENTAL,
    MIGRATE_SESSION_LOG_MOOD,
    CREATE_SESSIONS_TABLE,
    CREATE_TILT_CHECKS_TABLE,
)

logger = logging.getLogger(__name__)


def _get_default_db_path() -> Path:
    """Determine the best data directory for the current platform."""
    app_name = "LoLReview"
    if os.name == "nt":
        base = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    else:
        base = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    return base / app_name / "lol_review.db"


DEFAULT_DB_PATH = _get_default_db_path()


class ConnectionManager:
    """Manages the SQLite connection, schema creation, and legacy migration."""

    def __init__(self, db_path: Optional[Path] = None):
        self.db_path = db_path or DEFAULT_DB_PATH
        self.db_path.parent.mkdir(parents=True, exist_ok=True)

        # Migration: prefer the legacy data/ DB if it has newer data
        if db_path is None:
            legacy = self._find_legacy_db()
            if legacy:
                self._migrate_from_legacy(legacy)

        self._conn: Optional[sqlite3.Connection] = None
        self._init_db()

    def get_conn(self) -> sqlite3.Connection:
        """Get (or create) the shared database connection."""
        if self._conn is None:
            self._conn = sqlite3.connect(str(self.db_path), check_same_thread=False)
            self._conn.row_factory = sqlite3.Row
            self._conn.execute("PRAGMA journal_mode=WAL")
            self._conn.execute("PRAGMA busy_timeout=5000")
        return self._conn

    def close(self):
        """Close the database connection."""
        if self._conn:
            self._conn.close()
            self._conn = None

    # ── Schema initialisation ────────────────────────────────────────

    def _init_db(self):
        """Create tables if they don't exist."""
        conn = self.get_conn()
        conn.execute(CREATE_GAMES_TABLE)
        conn.execute(CREATE_TAGS_TABLE)
        conn.execute(CREATE_SESSION_LOG_TABLE)
        conn.execute(CREATE_PERSISTENT_NOTES_TABLE)
        conn.execute(CREATE_VOD_FILES_TABLE)
        conn.execute(CREATE_VOD_BOOKMARKS_TABLE)
        conn.execute(CREATE_GAME_EVENTS_TABLE)
        conn.execute(CREATE_GAME_EVENTS_INDEX)
        conn.execute(CREATE_OBJECTIVES_TABLE)
        conn.execute(CREATE_GAME_OBJECTIVES_TABLE)
        conn.execute(CREATE_CONCEPT_TAGS_TABLE)
        conn.execute(CREATE_GAME_CONCEPT_TAGS_TABLE)
        conn.execute(CREATE_RULES_TABLE)
        conn.execute(CREATE_DERIVED_EVENT_DEFINITIONS_TABLE)
        conn.execute(CREATE_DERIVED_EVENT_INSTANCES_TABLE)
        conn.execute(CREATE_OBJECTIVE_PROMPTS_TABLE)
        conn.execute(CREATE_PROMPT_ANSWERS_TABLE)
        conn.execute(CREATE_MATCHUP_NOTES_TABLE)

        conn.execute(CREATE_TILT_CHECKS_TABLE)

        # Migrate: add enemy_laner column to games if missing
        for stmt in MIGRATE_GAMES_ENEMY_LANER:
            try:
                conn.execute(stmt)
            except sqlite3.OperationalError:
                pass  # Column already exists

        # Migrate: add spotted_problems column to games if missing
        for stmt in MIGRATE_GAMES_SPOTTED_PROBLEMS:
            try:
                conn.execute(stmt)
            except sqlite3.OperationalError:
                pass  # Column already exists

        # Migrate: add reappraisal columns to games if missing
        for stmt in MIGRATE_GAMES_REAPPRAISAL:
            try:
                conn.execute(stmt)
            except sqlite3.OperationalError:
                pass

        # Migrate: add attribution column to games if missing
        for stmt in MIGRATE_GAMES_ATTRIBUTION:
            try:
                conn.execute(stmt)
            except sqlite3.OperationalError:
                pass

        # Migrate: add self-efficacy column to games if missing
        for stmt in MIGRATE_GAMES_SELF_EFFICACY:
            try:
                conn.execute(stmt)
            except sqlite3.OperationalError:
                pass

        # Migrate: add pre_game_mood to session_log if missing
        for stmt in MIGRATE_SESSION_LOG_MOOD:
            try:
                conn.execute(stmt)
            except sqlite3.OperationalError:
                pass

        # Create sessions table for session-level intentions/debriefs
        conn.execute(CREATE_SESSIONS_TABLE)

        # Migrate: add clip columns to vod_bookmarks if missing
        for stmt in MIGRATE_BOOKMARKS_CLIP_COLUMNS:
            try:
                conn.execute(stmt)
            except sqlite3.OperationalError:
                pass  # Column already exists

        # Migrate: add mental intention columns to session_log if missing
        for stmt in MIGRATE_SESSION_LOG_MENTAL:
            try:
                conn.execute(stmt)
            except sqlite3.OperationalError:
                pass  # Column already exists

        # Insert default tags if the table is empty
        cursor = conn.execute("SELECT COUNT(*) FROM tags")
        if cursor.fetchone()[0] == 0:
            conn.executemany(
                "INSERT OR IGNORE INTO tags (name, color) VALUES (?, ?)",
                DEFAULT_TAGS,
            )

        # Seed default concept tags if the table is empty
        cursor = conn.execute("SELECT COUNT(*) FROM concept_tags")
        if cursor.fetchone()[0] == 0:
            conn.executemany(
                "INSERT OR IGNORE INTO concept_tags (name, polarity, color) VALUES (?, ?, ?)",
                DEFAULT_CONCEPT_TAGS,
            )

        # Seed default derived event definitions if the table is empty
        cursor = conn.execute("SELECT COUNT(*) FROM derived_event_definitions")
        if cursor.fetchone()[0] == 0:
            conn.executemany(
                """INSERT OR IGNORE INTO derived_event_definitions
                   (name, source_types, min_count, window_seconds, color, is_default, created_at)
                   VALUES (?, ?, ?, ?, ?, 1, ?)""",
                [(n, st, mc, ws, c, int(time.time())) for n, st, mc, ws, c in DEFAULT_DERIVED_EVENTS],
            )

        # Insert default persistent notes row if empty
        cursor = conn.execute("SELECT COUNT(*) FROM persistent_notes")
        if cursor.fetchone()[0] == 0:
            conn.execute(
                "INSERT INTO persistent_notes (content, updated_at) VALUES (?, ?)",
                ("", int(time.time())),
            )

        # Backfill objectives.game_count from game_objectives for existing data
        conn.execute("""
            UPDATE objectives SET game_count = (
                SELECT COUNT(*) FROM game_objectives
                WHERE game_objectives.objective_id = objectives.id
            ) WHERE game_count = 0 AND EXISTS (
                SELECT 1 FROM game_objectives
                WHERE game_objectives.objective_id = objectives.id
            )
        """)

        conn.commit()
        logger.info(f"Database initialized at {self.db_path}")

    # ── Legacy migration ─────────────────────────────────────────────

    @staticmethod
    def _find_legacy_db() -> Optional[Path]:
        """Search common locations for a pre-AppData database file."""
        exe_dir = Path(sys.executable).parent
        candidates = [
            exe_dir / "data" / "lol_review.db",
            exe_dir.parent.parent / "data" / "lol_review.db",
            exe_dir.parent / "data" / "lol_review.db",
            Path(__file__).parent.parent.parent / "data" / "lol_review.db",
            Path.cwd() / "data" / "lol_review.db",
        ]
        for path in candidates:
            resolved = path.resolve()
            logger.info(f"Legacy DB candidate: {resolved} exists={resolved.exists()}")
            if resolved.exists():
                logger.info(f"Found legacy DB at {resolved}")
                return resolved
        return None

    def _migrate_from_legacy(self, legacy: Path):
        """Replace the AppData DB with the legacy one if it has more data."""
        import shutil

        if not self.db_path.exists():
            shutil.copy2(str(legacy), str(self.db_path))
            logger.info(f"Migrated database from {legacy} → {self.db_path}")
            return

        try:
            legacy_count = self._count_games(legacy)
            appdata_count = self._count_games(self.db_path)

            if legacy_count > appdata_count:
                backup = self.db_path.with_suffix(".db.bak")
                shutil.copy2(str(self.db_path), str(backup))
                shutil.copy2(str(legacy), str(self.db_path))
                logger.info(
                    f"Legacy DB has more data ({legacy_count} vs {appdata_count} games). "
                    f"Replaced AppData DB. Backup at {backup}"
                )
            else:
                logger.info(
                    f"AppData DB is up-to-date ({appdata_count} >= {legacy_count} games). "
                    f"No migration needed."
                )
        except Exception as exc:
            logger.warning(f"Migration comparison failed: {exc}")

    @staticmethod
    def _count_games(db_file: Path) -> int:
        """Count rows in the games table of a database file."""
        conn = sqlite3.connect(str(db_file))
        try:
            cur = conn.execute("SELECT COUNT(*) FROM games")
            return cur.fetchone()[0]
        except sqlite3.OperationalError:
            return 0
        finally:
            conn.close()

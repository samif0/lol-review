"""SQLite access for the coach sidecar.

Safety model (per COACH_PLAN.md §7 Phase 0 task 6, amendment 2026-04-18):

1. Core tables are READ-ONLY from Python. `read_core()` returns a read-only
   connection; attempting to open a write connection against core tables raises.
2. Coach-owned tables are listed in COACH_TABLES. Writes outside this allowlist
   raise AllowlistViolation.
3. DDL (ALTER/DROP/CREATE) against non-coach tables raises. Migrations may only
   CREATE TABLE IF NOT EXISTS, and only against COACH_TABLES.
4. Before running any migration, a timestamped backup of lol_review.db is made
   next to the DB, and the last 5 backups are retained.

This is a Python-side guard. The C# side does not need changes — its usual
repositories continue to write core tables.
"""

from __future__ import annotations

import logging
import re
import shutil
import sqlite3
import threading
from contextlib import contextmanager
from datetime import UTC, datetime
from pathlib import Path
from typing import Iterator

from coach.config import backups_dir, db_path

logger = logging.getLogger(__name__)


# Coach-owned tables. New-architecture tables from COACH_PLAN.md §4.
# Legacy coach_* tables (coach_players, coach_moments, etc.) are intentionally
# NOT in this list — nothing in the new pipeline writes them.
COACH_TABLES: frozenset[str] = frozenset(
    {
        "game_summary",
        "review_concepts",
        "user_concept_profile",
        "feature_values",
        "user_signal_ranking",
        "clip_frame_descriptions",
        "coach_sessions",
        "coach_response_edits",
        # Chat (phase-2-reshape, 2026-04-18): persistent conversation history.
        "coach_chat_threads",
        "coach_chat_messages",
        # Internal: tracks which migrations have been applied.
        "coach_migrations_applied",
    }
)


# Core tables the coach needs read access to. Source of truth is lol_review.db;
# listed here for documentation. This list is not used to enforce anything — any
# SELECT against any table is fine; we only block non-SELECT on non-COACH tables.
CORE_READ_TABLES: frozenset[str] = frozenset(
    {
        "games",
        "session_log",
        "matchup_notes",
        "vod_files",
        "vod_bookmarks",
        "game_events",
        "derived_event_instances",
        "objectives",
        "rules",
        "concept_tags",
        "game_objectives",
    }
)


class AllowlistViolation(RuntimeError):
    """Raised when a write or DDL targets a table outside the coach allowlist."""


# Regexes for auditing SQL before execution. These are a defense-in-depth check,
# not a full parser — callers should still use parameterized queries and the
# connection wrappers. The intent: make a destructive typo against a non-coach
# table impossible.
_WRITE_STMT_RE = re.compile(
    r"^\s*(INSERT|UPDATE|DELETE|REPLACE)\b\s*(?:INTO\s+|OR\s+(?:REPLACE|IGNORE|ABORT|FAIL|ROLLBACK)\s+INTO\s+|FROM\s+)?[\"`\[]?(\w+)",
    re.IGNORECASE,
)
_DDL_STMT_RE = re.compile(
    r"^\s*(CREATE|ALTER|DROP)\s+(?:TEMP\s+|TEMPORARY\s+)?(?:TABLE|INDEX|VIEW|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+|IF\s+EXISTS\s+)?[\"`\[]?(\w+)",
    re.IGNORECASE,
)


def _audit_sql(sql: str) -> None:
    """Validate a SQL statement against the coach allowlist.

    Raises AllowlistViolation for any INSERT/UPDATE/DELETE/REPLACE against a
    table not in COACH_TABLES, or any DDL against such a table.

    SELECT is always allowed (the coach reads core tables).
    """

    stripped = sql.strip()
    write_match = _WRITE_STMT_RE.match(stripped)
    if write_match:
        table = write_match.group(2).lower()
        if table not in COACH_TABLES:
            raise AllowlistViolation(
                f"Blocked {write_match.group(1).upper()} against non-coach table '{table}'. "
                f"Allowed coach tables: {sorted(COACH_TABLES)}."
            )

    ddl_match = _DDL_STMT_RE.match(stripped)
    if ddl_match:
        verb = ddl_match.group(1).upper()
        name = ddl_match.group(2).lower()
        # For DDL against tables, name is the table. For indexes/triggers, name
        # is their own identifier — still allowed if the body targets a coach
        # table, but we keep this conservative: allowlist the identifier itself.
        # Index names for coach tables are prefixed `idx_{tablename}_*` by
        # convention in migrations; we accept any name containing a coach table
        # substring, or the name itself being a coach table.
        is_coach_table = name in COACH_TABLES
        references_coach_table = any(t in stripped.lower() for t in COACH_TABLES)
        if not (is_coach_table or references_coach_table):
            raise AllowlistViolation(
                f"Blocked {verb} against non-coach table/index '{name}'. "
                f"Coach migrations must only touch: {sorted(COACH_TABLES)}."
            )


class SafeWriteConnection:
    """Wraps sqlite3.Connection. Audits every execute() call."""

    def __init__(self, conn: sqlite3.Connection) -> None:
        self._conn = conn

    def execute(self, sql: str, parameters: tuple | dict | None = None) -> sqlite3.Cursor:
        _audit_sql(sql)
        if parameters is None:
            return self._conn.execute(sql)
        return self._conn.execute(sql, parameters)

    def executemany(self, sql: str, seq: list | tuple) -> sqlite3.Cursor:
        _audit_sql(sql)
        return self._conn.executemany(sql, seq)

    def executescript(self, script: str) -> sqlite3.Cursor:
        # Split on semicolon for audit. This is conservative.
        for stmt in script.split(";"):
            if stmt.strip():
                _audit_sql(stmt)
        return self._conn.executescript(script)

    def commit(self) -> None:
        self._conn.commit()

    def rollback(self) -> None:
        self._conn.rollback()

    def close(self) -> None:
        self._conn.close()

    def __enter__(self) -> "SafeWriteConnection":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if exc_type is None:
            self._conn.commit()
        else:
            self._conn.rollback()
        self._conn.close()


_migration_lock = threading.Lock()
_migrations_applied = False


def _open_readonly() -> sqlite3.Connection:
    uri = f"file:{db_path()}?mode=ro"
    conn = sqlite3.connect(uri, uri=True, timeout=5.0)
    conn.row_factory = sqlite3.Row
    return conn


def _open_readwrite() -> sqlite3.Connection:
    path = db_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(path), timeout=5.0, isolation_level="DEFERRED")
    conn.row_factory = sqlite3.Row
    # WAL is safe for concurrent C# writes.
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA foreign_keys=ON")
    return conn


@contextmanager
def read_core() -> Iterator[sqlite3.Connection]:
    """Open a read-only connection for querying core tables."""
    ensure_migrations_applied()
    conn = _open_readonly()
    try:
        yield conn
    finally:
        conn.close()


@contextmanager
def write_coach() -> Iterator[SafeWriteConnection]:
    """Open a guarded read-write connection for coach tables only."""
    ensure_migrations_applied()
    raw = _open_readwrite()
    wrapped = SafeWriteConnection(raw)
    try:
        yield wrapped
    except Exception:
        wrapped.rollback()
        wrapped.close()
        raise
    else:
        wrapped.commit()
        wrapped.close()


def backup_database() -> Path | None:
    """Copy lol_review.db to backups/ with a timestamp. Retain last 5.

    Returns the path of the new backup, or None if the source DB doesn't exist
    yet (first run).
    """

    src = db_path()
    if not src.exists():
        return None

    backups = backups_dir()
    backups.mkdir(parents=True, exist_ok=True)

    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    dst = backups / f"coach-pre-migration-{stamp}.db"

    shutil.copy2(src, dst)
    logger.info("DB backup created: %s", dst)

    # Rotate: keep last 5 coach pre-migration backups.
    existing = sorted(backups.glob("coach-pre-migration-*.db"), key=lambda p: p.stat().st_mtime)
    for old in existing[:-5]:
        try:
            old.unlink()
            logger.debug("Rotated out old backup: %s", old)
        except OSError:
            logger.warning("Could not delete old backup %s", old)

    return dst


def ensure_migrations_applied() -> None:
    """Run coach migrations (additive only) with a pre-backup. Idempotent."""

    global _migrations_applied
    if _migrations_applied:
        return

    with _migration_lock:
        if _migrations_applied:
            return

        backup_database()

        migrations = _discover_migrations()
        raw = _open_readwrite()
        wrapped = SafeWriteConnection(raw)
        try:
            wrapped.execute(
                """
                CREATE TABLE IF NOT EXISTS coach_sessions (
                    id INTEGER PRIMARY KEY,
                    mode TEXT NOT NULL,
                    scope_json TEXT NOT NULL,
                    context_json TEXT NOT NULL,
                    response_text TEXT NOT NULL,
                    response_json TEXT,
                    model_name TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    latency_ms INTEGER,
                    created_at INTEGER NOT NULL
                )
                """
            )
            wrapped.execute(
                """
                CREATE TABLE IF NOT EXISTS coach_migrations_applied (
                    filename TEXT PRIMARY KEY,
                    applied_at INTEGER NOT NULL
                )
                """
            )

            for path in migrations:
                already = raw.execute(
                    "SELECT 1 FROM coach_migrations_applied WHERE filename = ?",
                    (path.name,),
                ).fetchone()
                if already:
                    continue
                sql = path.read_text(encoding="utf-8")
                for stmt in sql.split(";"):
                    stripped = stmt.strip()
                    if not stripped:
                        continue
                    wrapped.execute(stripped)
                wrapped.execute(
                    "INSERT INTO coach_migrations_applied (filename, applied_at) VALUES (?, strftime('%s','now'))",
                    (path.name,),
                )
                logger.info("Applied coach migration: %s", path.name)

            wrapped.commit()
        except Exception:
            wrapped.rollback()
            wrapped.close()
            raise
        else:
            wrapped.close()

        _migrations_applied = True


def _discover_migrations() -> list[Path]:
    migrations_dir = Path(__file__).parent / "migrations"
    if not migrations_dir.exists():
        return []
    return sorted(migrations_dir.glob("[0-9]*.sql"))


# ──────────────────────────────────────────────────────────────────────
# Read helpers (core tables)
# ──────────────────────────────────────────────────────────────────────


def list_game_ids(since: int | None = None) -> list[int]:
    """Return game IDs, optionally filtered to games created on/after a unix timestamp."""
    with read_core() as conn:
        if since is None:
            rows = conn.execute("SELECT id FROM games ORDER BY id").fetchall()
        else:
            rows = conn.execute(
                "SELECT id FROM games WHERE COALESCE(ended_at, created_at, 0) >= ? ORDER BY id",
                (since,),
            ).fetchall()
        return [int(r["id"]) for r in rows]


def fetch_game_row(game_id: int) -> dict | None:
    with read_core() as conn:
        row = conn.execute("SELECT * FROM games WHERE id = ?", (game_id,)).fetchone()
        return dict(row) if row else None


def fetch_session_log_row(game_id: int) -> dict | None:
    with read_core() as conn:
        row = conn.execute(
            "SELECT * FROM session_log WHERE game_id = ? ORDER BY id DESC LIMIT 1", (game_id,)
        ).fetchone()
        return dict(row) if row else None


def fetch_matchup_note(champion: str, enemy: str) -> dict | None:
    with read_core() as conn:
        row = conn.execute(
            "SELECT * FROM matchup_notes WHERE champion = ? AND enemy = ? ORDER BY id DESC LIMIT 1",
            (champion, enemy),
        ).fetchone()
        return dict(row) if row else None


def fetch_game_events(game_id: int) -> list[dict]:
    """game_events.game_id is the Riot id (FK to games.game_id). Accept
    either identifier — if caller passes a PK, resolve to Riot id first.
    Orders by game_time_s (actual column name in the schema)."""
    riot_id = _to_riot_id(game_id)
    with read_core() as conn:
        rows = conn.execute(
            "SELECT * FROM game_events WHERE game_id = ? ORDER BY game_time_s",
            (riot_id,),
        ).fetchall()
        return [dict(r) for r in rows]


def fetch_derived_events(game_id: int) -> list[dict]:
    """derived_event_instances.game_id is the Riot id. Orders by start_time_s."""
    riot_id = _to_riot_id(game_id)
    with read_core() as conn:
        rows = conn.execute(
            "SELECT * FROM derived_event_instances WHERE game_id = ? ORDER BY start_time_s",
            (riot_id,),
        ).fetchall()
        return [dict(r) for r in rows]


def _to_riot_id(game_id: int) -> int:
    """Accept either games.id (PK) or games.game_id (Riot id); return Riot id.

    game_events + derived_event_instances join to games.game_id (per the FK
    declaration in Schema.cs), so we always need the Riot id when querying
    them. If the caller has a PK (from games.id), look up the Riot id; else
    assume the caller already has a Riot id and pass it through.
    """
    with read_core() as conn:
        # Try as Riot id (most common case — C# ViewModels pass games.game_id).
        row = conn.execute(
            "SELECT game_id FROM games WHERE game_id = ? LIMIT 1", (game_id,)
        ).fetchone()
        if row is not None:
            return int(row["game_id"])
        # Fall back: maybe caller passed the PK.
        row = conn.execute(
            "SELECT game_id FROM games WHERE id = ? LIMIT 1", (game_id,)
        ).fetchone()
        if row is not None and row["game_id"] is not None:
            return int(row["game_id"])
    return int(game_id)


def fetch_vod_bookmark(bookmark_id: int) -> dict | None:
    with read_core() as conn:
        row = conn.execute(
            "SELECT * FROM vod_bookmarks WHERE id = ?", (bookmark_id,)
        ).fetchone()
        return dict(row) if row else None


def fetch_vod_for_game(game_id: int) -> dict | None:
    with read_core() as conn:
        row = conn.execute(
            "SELECT * FROM vod_files WHERE game_id = ? ORDER BY id DESC LIMIT 1", (game_id,)
        ).fetchone()
        return dict(row) if row else None

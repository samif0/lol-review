"""Game storage and retrieval."""

import json
import logging
from datetime import datetime, timedelta
from typing import Optional

from .connection import ConnectionManager
from ..constants import CASUAL_MODES, CASUAL_MODE_SQL_FILTER, DEFAULT_RECENT_GAMES_LIMIT, UNREVIEWED_GAMES_DAYS

logger = logging.getLogger(__name__)


class GameRepository:
    """CRUD operations for the games table."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def save(self, stats) -> int:
        """Save game stats to the database. Returns the row ID.

        Casual modes (ARAM, Arena, etc.) are silently skipped — they
        don't belong in a ranked improvement tracker.
        """
        if getattr(stats, "game_mode", "").upper() in CASUAL_MODES:
            logger.info(f"Skipping casual game: {stats.champion_name} ({stats.game_mode})")
            return -1

        conn = self._conn_mgr.get_conn()

        existing = conn.execute(
            "SELECT id FROM games WHERE game_id = ?", (stats.game_id,)
        ).fetchone()
        if existing:
            logger.info(f"Game {stats.game_id} already saved (row {existing[0]})")
            return existing[0]

        date_str = datetime.fromtimestamp(stats.timestamp).strftime("%Y-%m-%d %H:%M")

        cursor = conn.execute(
            """INSERT INTO games (
                game_id, timestamp, date_played, game_duration, game_mode,
                game_type, queue_type, summoner_name, champion_name, champion_id,
                team_id, position, role, win,
                kills, deaths, assists, kda_ratio,
                largest_killing_spree, largest_multi_kill,
                double_kills, triple_kills, quadra_kills, penta_kills, first_blood,
                total_damage_dealt, total_damage_to_champions,
                physical_damage_to_champions, magic_damage_to_champions,
                true_damage_to_champions, total_damage_taken,
                damage_self_mitigated, largest_critical_strike,
                gold_earned, gold_spent,
                total_minions_killed, neutral_minions_killed, cs_total, cs_per_min,
                vision_score, wards_placed, wards_killed, control_wards_purchased,
                turret_kills, inhibitor_kills, dragon_kills, baron_kills,
                rift_herald_kills,
                total_heal, total_heals_on_teammates,
                total_damage_shielded_on_teammates,
                total_time_cc_dealt, time_ccing_others,
                spell1_casts, spell2_casts, spell3_casts, spell4_casts,
                summoner1_id, summoner2_id, items,
                champ_level, team_kills, kill_participation,
                raw_stats, enemy_laner
            ) VALUES (
                ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,
                ?, ?, ?, ?,
                ?, ?, ?, ?,
                ?, ?,
                ?, ?, ?, ?, ?,
                ?, ?,
                ?, ?,
                ?, ?,
                ?, ?,
                ?, ?,
                ?, ?, ?, ?,
                ?, ?, ?, ?,
                ?, ?, ?, ?,
                ?,
                ?, ?,
                ?,
                ?, ?,
                ?, ?, ?, ?,
                ?, ?, ?,
                ?, ?, ?,
                ?, ?
            )""",
            (
                stats.game_id, stats.timestamp, date_str, stats.game_duration,
                stats.game_mode, stats.game_type, stats.queue_type,
                stats.summoner_name, stats.champion_name, stats.champion_id,
                stats.team_id, stats.position, stats.role, int(stats.win),
                stats.kills, stats.deaths, stats.assists, stats.kda_ratio,
                stats.largest_killing_spree, stats.largest_multi_kill,
                stats.double_kills, stats.triple_kills, stats.quadra_kills,
                stats.penta_kills, int(stats.first_blood),
                stats.total_damage_dealt, stats.total_damage_to_champions,
                stats.physical_damage_to_champions, stats.magic_damage_to_champions,
                stats.true_damage_to_champions, stats.total_damage_taken,
                stats.damage_self_mitigated, stats.largest_critical_strike,
                stats.gold_earned, stats.gold_spent,
                stats.total_minions_killed, stats.neutral_minions_killed,
                stats.cs_total, stats.cs_per_min,
                stats.vision_score, stats.wards_placed, stats.wards_killed,
                stats.control_wards_purchased,
                stats.turret_kills, stats.inhibitor_kills, stats.dragon_kills,
                stats.baron_kills, stats.rift_herald_kills,
                stats.total_heal, stats.total_heals_on_teammates,
                stats.total_damage_shielded_on_teammates,
                stats.total_time_cc_dealt, stats.time_ccing_others,
                stats.spell1_casts, stats.spell2_casts, stats.spell3_casts,
                stats.spell4_casts,
                stats.summoner1_id, stats.summoner2_id,
                json.dumps(stats.items),
                stats.champ_level, stats.team_kills, stats.kill_participation,
                json.dumps(stats.raw_stats), stats.enemy_laner,
            ),
        )
        conn.commit()
        row_id = cursor.lastrowid
        logger.info(f"Saved game {stats.game_id} as row {row_id}")
        return row_id

    def update_review(
        self,
        game_id: int,
        notes: str = "",
        rating: int = 0,
        tags: list[str] = None,
        mistakes: str = "",
        went_well: str = "",
        focus_next: str = "",
        spotted_problems: str = "",
        outside_control: str = "",
        within_control: str = "",
        attribution: str = "",
        personal_contribution: str = "",
        **kwargs,
    ):
        """Update the review fields for a game (by game_id, not row id)."""
        conn = self._conn_mgr.get_conn()
        conn.execute(
            """UPDATE games SET
                review_notes = ?,
                rating = ?,
                tags = ?,
                mistakes = ?,
                went_well = ?,
                focus_next = ?,
                spotted_problems = ?,
                outside_control = ?,
                within_control = ?,
                attribution = ?,
                personal_contribution = ?
            WHERE game_id = ?""",
            (
                notes,
                rating,
                json.dumps(tags or []),
                mistakes,
                went_well,
                focus_next,
                spotted_problems,
                outside_control,
                within_control,
                attribution,
                personal_contribution,
                game_id,
            ),
        )
        conn.commit()

    def get(self, game_id: int) -> Optional[dict]:
        """Get a single game by game_id."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            "SELECT * FROM games WHERE game_id = ?", (game_id,)
        ).fetchone()
        return dict(row) if row else None

    def get_recent(self, limit: int = DEFAULT_RECENT_GAMES_LIMIT, offset: int = 0) -> list[dict]:
        """Get recent ranked/normal games ordered by most recent first."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            f"SELECT * FROM games WHERE 1=1 {CASUAL_MODE_SQL_FILTER} ORDER BY timestamp DESC LIMIT ? OFFSET ?",
            (limit, offset),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_champion_stats(self) -> list[dict]:
        """Aggregate stats grouped by champion (excludes casual modes)."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(f"""
            SELECT
                champion_name,
                COUNT(*) as games_played,
                SUM(win) as wins,
                ROUND(AVG(win) * 100, 1) as winrate,
                ROUND(AVG(kills), 1) as avg_kills,
                ROUND(AVG(deaths), 1) as avg_deaths,
                ROUND(AVG(assists), 1) as avg_assists,
                ROUND(AVG(kda_ratio), 2) as avg_kda,
                ROUND(AVG(cs_per_min), 1) as avg_cs_min,
                ROUND(AVG(vision_score), 1) as avg_vision,
                ROUND(AVG(total_damage_to_champions), 0) as avg_damage
            FROM games
            WHERE 1=1 {CASUAL_MODE_SQL_FILTER}
            GROUP BY champion_name
            ORDER BY games_played DESC
        """).fetchall()
        return [dict(r) for r in rows]

    def get_overall_stats(self) -> dict:
        """Get aggregate stats across all ranked/normal games."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(f"""
            SELECT
                COUNT(*) as total_games,
                SUM(win) as total_wins,
                ROUND(AVG(win) * 100, 1) as winrate,
                ROUND(AVG(kills), 1) as avg_kills,
                ROUND(AVG(deaths), 1) as avg_deaths,
                ROUND(AVG(assists), 1) as avg_assists,
                ROUND(AVG(kda_ratio), 2) as avg_kda,
                ROUND(AVG(cs_per_min), 1) as avg_cs_min,
                ROUND(AVG(vision_score), 1) as avg_vision,
                SUM(penta_kills) as total_pentas,
                SUM(quadra_kills) as total_quadras,
                MAX(kills) as max_kills,
                MAX(kda_ratio) as best_kda
            FROM games
            WHERE 1=1 {CASUAL_MODE_SQL_FILTER}
        """).fetchone()
        return dict(row) if row else {}

    def get_last_review_focus(self) -> dict:
        """Get the focus_next and mistakes from the most recent reviewed game."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(f"""
            SELECT focus_next, mistakes, went_well
            FROM games
            WHERE (focus_next != '' OR mistakes != '')
                {CASUAL_MODE_SQL_FILTER}
            ORDER BY timestamp DESC
            LIMIT 1
        """).fetchone()

        if row:
            return {
                "focus_next": row["focus_next"] or "",
                "mistakes": row["mistakes"] or "",
                "went_well": row["went_well"] or "",
            }
        return {"focus_next": "", "mistakes": "", "went_well": ""}

    def get_win_streak(self) -> int:
        """Get current win/loss streak. Positive = wins, negative = losses."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            f"SELECT win FROM games WHERE 1=1 {CASUAL_MODE_SQL_FILTER} ORDER BY timestamp DESC LIMIT 50"
        ).fetchall()

        if not rows:
            return 0

        streak = 0
        first_result = rows[0]["win"]
        for row in rows:
            if row["win"] == first_result:
                streak += 1
            else:
                break

        return streak if first_result else -streak

    def get_losses(self, champion: Optional[str] = None) -> list[dict]:
        """Get all losses, optionally filtered by champion (excludes casual modes)."""
        conn = self._conn_mgr.get_conn()

        if champion and champion != "All Champions":
            rows = conn.execute(
                f"""SELECT * FROM games
                WHERE win = 0 AND champion_name = ? {CASUAL_MODE_SQL_FILTER}
                ORDER BY timestamp DESC""",
                (champion,),
            ).fetchall()
        else:
            rows = conn.execute(
                f"""SELECT * FROM games
                WHERE win = 0 {CASUAL_MODE_SQL_FILTER}
                ORDER BY timestamp DESC"""
            ).fetchall()

        return [dict(r) for r in rows]

    def get_unreviewed_games(self, days: int = UNREVIEWED_GAMES_DAYS) -> list[dict]:
        """Get recent ranked/normal games that haven't been reviewed yet.

        A game counts as 'unreviewed' if it has no rating, no mistakes text,
        no went_well text, and no focus_next text.
        """
        conn = self._conn_mgr.get_conn()
        cutoff = datetime.now() - timedelta(days=days)
        cutoff_ts = int(cutoff.timestamp())

        rows = conn.execute(
            f"""SELECT * FROM games
            WHERE timestamp >= ?
              AND (rating IS NULL OR rating = 0)
              AND (mistakes IS NULL OR mistakes = '')
              AND (went_well IS NULL OR went_well = '')
              AND (focus_next IS NULL OR focus_next = '')
              {CASUAL_MODE_SQL_FILTER}
            ORDER BY timestamp DESC""",
            (cutoff_ts,),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_games_for_date(self, date_str: str) -> list[dict]:
        """Get ranked/normal games played on a specific date (YYYY-MM-DD)."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            f"""SELECT * FROM games
            WHERE date_played LIKE ? {CASUAL_MODE_SQL_FILTER}
            ORDER BY timestamp ASC""",
            (f"{date_str}%",),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_todays_games(self) -> list[dict]:
        """Get ranked/normal games played today."""
        from datetime import datetime
        conn = self._conn_mgr.get_conn()
        today_start = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
        today_timestamp = int(today_start.timestamp())

        rows = conn.execute(
            f"""SELECT * FROM games
            WHERE timestamp >= ? {CASUAL_MODE_SQL_FILTER}
            ORDER BY timestamp ASC""",
            (today_timestamp,),
        ).fetchall()

        return [dict(r) for r in rows]

    def get_unique_champions(self, losses_only: bool = False) -> list[str]:
        """Get list of unique champion names from ranked/normal games."""
        conn = self._conn_mgr.get_conn()

        if losses_only:
            rows = conn.execute(
                f"""SELECT DISTINCT champion_name
                FROM games
                WHERE win = 0 {CASUAL_MODE_SQL_FILTER}
                ORDER BY champion_name"""
            ).fetchall()
        else:
            rows = conn.execute(
                f"""SELECT DISTINCT champion_name
                FROM games
                WHERE 1=1 {CASUAL_MODE_SQL_FILTER}
                ORDER BY champion_name"""
            ).fetchall()

        return [row[0] for row in rows]

    def save_manual(
        self,
        champion_name: str,
        win: bool,
        kills: int = 0,
        deaths: int = 0,
        assists: int = 0,
        game_mode: str = "Manual Entry",
        notes: str = "",
        mistakes: str = "",
        went_well: str = "",
        focus_next: str = "",
        rating: int = 0,
        tags: list[str] = None,
    ) -> int:
        """Save a manually entered game with minimal required fields."""
        import time as _time
        conn = self._conn_mgr.get_conn()

        now = int(_time.time())
        game_id = now
        kda_ratio = (kills + assists) / max(deaths, 1)
        date_str = datetime.now().strftime("%Y-%m-%d %H:%M")

        cursor = conn.execute(
            """INSERT INTO games (
                game_id, timestamp, date_played, game_duration, game_mode,
                game_type, queue_type, summoner_name, champion_name, champion_id,
                team_id, position, role, win,
                kills, deaths, assists, kda_ratio,
                review_notes, rating, tags, mistakes, went_well, focus_next
            ) VALUES (
                ?, ?, ?, ?, ?,
                ?, ?, ?, ?, ?,
                ?, ?, ?, ?,
                ?, ?, ?, ?,
                ?, ?, ?, ?, ?, ?
            )""",
            (
                game_id, now, date_str, 0, game_mode,
                "Manual", "Manual", "Manual Entry", champion_name, 0,
                0, "", "", int(win),
                kills, deaths, assists, round(kda_ratio, 2),
                notes, rating, json.dumps(tags or []),
                mistakes, went_well, focus_next,
            ),
        )
        conn.commit()
        logger.info(f"Saved manual game entry for {champion_name} (game_id={game_id})")
        return game_id

    def update_enemy_laner(self, game_id: int, enemy_laner: str):
        """Update the enemy_laner field for a game."""
        conn = self._conn_mgr.get_conn()
        conn.execute(
            "UPDATE games SET enemy_laner = ? WHERE game_id = ?",
            (enemy_laner, game_id),
        )
        conn.commit()

    def get_attribution_stats(self) -> list[dict]:
        """Get win/loss breakdown by attribution value."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            f"""SELECT attribution,
                COUNT(*) as games,
                SUM(win) as wins,
                COUNT(*) - SUM(win) as losses,
                ROUND(AVG(win) * 100, 1) as winrate
            FROM games
            WHERE attribution IS NOT NULL AND attribution != ''
              {CASUAL_MODE_SQL_FILTER}
            GROUP BY attribution
            ORDER BY games DESC"""
        ).fetchall()
        return [dict(r) for r in rows]

    def get_recent_spotted_problems(self, limit: int = 20) -> list[dict]:
        """Get recent games that have spotted_problems notes."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            f"""SELECT game_id, champion_name, spotted_problems, date_played, win
                FROM games
                WHERE spotted_problems IS NOT NULL AND spotted_problems != ''
                  {CASUAL_MODE_SQL_FILTER}
                ORDER BY timestamp DESC
                LIMIT ?""",
            (limit,),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_recent_for_charts(self, limit: int = 100) -> list[dict]:
        """Get recent game data for trend charts."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            f"""SELECT game_id, win, deaths, timestamp, champion_name, kda_ratio
                FROM games
                WHERE 1=1 {CASUAL_MODE_SQL_FILTER}
                ORDER BY timestamp DESC
                LIMIT ?""",
            (limit,),
        ).fetchall()
        return [dict(r) for r in reversed(rows)]

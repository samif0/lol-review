"""Claude context generation from session data."""

from .connection import ConnectionManager
from .games import GameRepository
from .notes import NotesRepository
from .session_log import SessionLogRepository


def generate_claude_context(
    conn_mgr: ConnectionManager,
    games: GameRepository,
    session_log: SessionLogRepository,
    notes: NotesRepository,
) -> str:
    """Generate a paste-ready text block for a new Claude conversation."""
    lines = []
    lines.append("=" * 60)
    lines.append("LEAGUE OF LEGENDS — SESSION LOG & CONTEXT")
    lines.append("=" * 60)
    lines.append("")

    # Adherence streak
    streak = session_log.get_adherence_streak()
    lines.append(f"Schedule Adherence Streak: {streak} clean play-day(s)")
    lines.append("")

    # Mental-winrate correlation
    lines.append("--- MENTAL STATE vs WINRATE ---")
    correlations = session_log.get_mental_winrate_correlation()
    if correlations:
        for c in correlations:
            lines.append(
                f"  Mental {c['bracket']}: {c['games']} games, "
                f"{c['wins']} wins, {c['winrate']}% WR"
            )
    else:
        lines.append("  No data yet.")
    lines.append("")

    # Last 7 days of session logs
    lines.append("--- LAST 7 DAYS ---")
    summaries = session_log.get_daily_summaries(7)
    if summaries:
        for day in summaries:
            broke = " [RULE BROKEN]" if day["rule_breaks"] else ""
            champs = day.get("champions_played", "")
            lines.append(
                f"  {day['date']}: {day['games']}G "
                f"{day['wins']}W-{day['losses']}L  "
                f"avg mental {day['avg_mental']}"
                f"{broke}"
            )
            if champs:
                lines.append(f"    Champions: {champs}")
    else:
        lines.append("  No session data yet.")
    lines.append("")

    # Detailed game log
    lines.append("--- DETAILED GAME LOG ---")
    entries = session_log.get_range(7)
    current_date = ""
    if entries:
        for e in entries:
            if e["date"] != current_date:
                current_date = e["date"]
                lines.append(f"\n  [{current_date}]")

            result = "W" if e["win"] else "L"
            mental = e["mental_rating"]
            broke = " **RULE BREAK**" if e["rule_broken"] else ""
            note = e.get("improvement_note", "").strip()
            note_str = f' — "{note}"' if note else ""

            lines.append(
                f"    {e['champion_name']} {result} "
                f"(mental: {mental}/10){broke}{note_str}"
            )

            mental_handled = (e.get("mental_handled") or "").strip()
            if mental_handled:
                lines.append(f'      Mental handled: "{mental_handled}"')

            game = games.get(e.get("game_id")) if e.get("game_id") else None
            if game:
                mistakes = game.get("mistakes", "").strip()
                went_well = game.get("went_well", "").strip()
                focus_next = game.get("focus_next", "").strip()
                if mistakes:
                    lines.append(f'      Mistakes: "{mistakes}"')
                if went_well:
                    lines.append(f'      Went well: "{went_well}"')
                if focus_next:
                    lines.append(f'      Focus next: "{focus_next}"')
    else:
        lines.append("  No games logged yet.")
    lines.append("")

    # Persistent notes
    notes_content = notes.get()
    lines.append("--- MY NOTES / PATTERNS ---")
    if notes_content.strip():
        for line in notes_content.strip().split("\n"):
            lines.append(f"  {line}")
    else:
        lines.append("  (No persistent notes set yet)")
    lines.append("")

    lines.append("=" * 60)
    lines.append("Hold me accountable. Call out patterns you see.")
    lines.append("=" * 60)

    return "\n".join(lines)

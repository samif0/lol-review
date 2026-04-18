"""Chat coach — ask-on-demand, grounded in user data.

Replaces the mode-specific post_game/session/weekly drafting pattern
with a single chat interface. Context is retrieved automatically based
on the user's question and any scope the UI pinned to the thread.
"""

from __future__ import annotations

import json
import logging
import re
import time
from pathlib import Path
from typing import Any, AsyncIterator

from coach.config import load_config
from coach.db import read_core, write_coach
from coach.providers import get_provider
from coach.schemas import AskResponse, ChatMessage, LLMMessage, LLMRequest

logger = logging.getLogger(__name__)

_PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "ask.md"
_PROMPT_CACHE: str | None = None

# Keep a rolling window of the last N turns in the conversation so context
# doesn't balloon over long threads.
MAX_HISTORY_TURNS = 8


def _load_prompt() -> str:
    global _PROMPT_CACHE
    if _PROMPT_CACHE is None:
        _PROMPT_CACHE = _PROMPT_PATH.read_text(encoding="utf-8")
    return _PROMPT_CACHE


async def run_ask(
    question: str,
    thread_id: int | None = None,
    scope: dict[str, Any] | None = None,
) -> AskResponse:
    now = int(time.time())

    # Create or load thread
    if thread_id is None:
        thread_id = _create_thread(scope, now, title_from_question(question))
        scope_for_thread = scope
    else:
        scope_for_thread = _load_thread_scope(thread_id)

    # Merge any per-turn scope override (not currently used; leave for future)
    effective_scope = scope_for_thread or {}

    # Retrieve context
    context = _retrieve_context(question, effective_scope)

    # Append previous turns in the thread (rolling window)
    history = _load_recent_messages(thread_id, MAX_HISTORY_TURNS * 2)

    # Persist the user message now (so if the LLM call fails, the question
    # still shows up in the thread on next load — better UX than losing it).
    user_msg_id = _persist_message(
        thread_id=thread_id,
        role="user",
        content=question,
        context_json=json.dumps({"scope": effective_scope}),
        model_name=None,
        provider=None,
        latency_ms=None,
        input_tokens=None,
        output_tokens=None,
        created_at=now,
    )

    # Build the LLM request
    cfg = load_config()
    provider = get_provider()
    model = _pick_model(cfg)

    system_prompt = _load_prompt().replace(
        "{{context}}", json.dumps(context, indent=2, default=str)[:60000]
    ).replace("{{question}}", question)

    messages: list[LLMMessage] = [LLMMessage(role="user", content=system_prompt)]
    # Add prior conversation turns (except the one we just persisted — it's
    # already embedded in {{question}} for the grounded template).
    for m in history:
        messages.append(
            LLMMessage(role=m["role"], content=m["content"])
        )

    response = await provider.complete(
        LLMRequest(
            messages=messages,
            model=model,
            temperature=0.3,
            max_tokens=1500,
        )
    )

    assistant_now = int(time.time())
    assistant_msg_id = _persist_message(
        thread_id=thread_id,
        role="assistant",
        content=response.text,
        context_json=None,
        model_name=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
        input_tokens=response.input_tokens,
        output_tokens=response.output_tokens,
        created_at=assistant_now,
    )

    _touch_thread(thread_id, assistant_now)

    user_message = ChatMessage(
        id=user_msg_id,
        thread_id=thread_id,
        role="user",
        content=question,
        model=None,
        provider=None,
        latency_ms=None,
        created_at=now,
    )
    assistant_message = ChatMessage(
        id=assistant_msg_id,
        thread_id=thread_id,
        role="assistant",
        content=response.text,
        model=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
        created_at=assistant_now,
    )

    return AskResponse(
        thread_id=thread_id,
        user_message=user_message,
        assistant_message=assistant_message,
        coach_visible_totals=context.get("coach_visible_totals", {}),
    )


async def run_ask_stream(
    question: str,
    thread_id: int | None = None,
    scope: dict[str, Any] | None = None,
) -> AsyncIterator[str]:
    """Yield SSE-formatted lines for a streaming ask.

    Event shapes (all as `data: {json}\\n\\n`):
      {"type":"started", "thread_id":N, "user_message_id":N, "coach_visible_totals":{...}}
      {"type":"delta",   "text":"..."}            # one chunk of assistant text
      {"type":"done",    "assistant_message_id":N, "model":"...", "provider":"...", "latency_ms":N}
      {"type":"error",   "message":"..."}
    """

    try:
        now = int(time.time())

        # Thread handling (same as run_ask)
        if thread_id is None:
            thread_id = _create_thread(scope, now, title_from_question(question))
            scope_for_thread = scope
        else:
            scope_for_thread = _load_thread_scope(thread_id)

        effective_scope = scope_for_thread or {}
        context = _retrieve_context(question, effective_scope)
        history = _load_recent_messages(thread_id, MAX_HISTORY_TURNS * 2)

        user_msg_id = _persist_message(
            thread_id=thread_id,
            role="user",
            content=question,
            context_json=json.dumps({"scope": effective_scope}),
            model_name=None,
            provider=None,
            latency_ms=None,
            input_tokens=None,
            output_tokens=None,
            created_at=now,
        )

        yield _sse({
            "type": "started",
            "thread_id": thread_id,
            "user_message_id": user_msg_id,
            "coach_visible_totals": context.get("coach_visible_totals", {}),
        })

        cfg = load_config()
        provider = get_provider()
        model = _pick_model(cfg)

        system_prompt = _load_prompt().replace(
            "{{context}}", json.dumps(context, indent=2, default=str)[:60000]
        ).replace("{{question}}", question)

        messages: list[LLMMessage] = [LLMMessage(role="user", content=system_prompt)]
        for m in history:
            messages.append(LLMMessage(role=m["role"], content=m["content"]))

        req = LLMRequest(
            messages=messages,
            model=model,
            temperature=0.3,
            max_tokens=1500,
        )

        start = time.perf_counter()
        accumulated: list[str] = []

        try:
            async for chunk in provider.complete_stream(req):
                if chunk:
                    accumulated.append(chunk)
                    yield _sse({"type": "delta", "text": chunk})
        except Exception as exc:
            logger.exception("stream failed")
            yield _sse({"type": "error", "message": str(exc)})
            return

        full_text = "".join(accumulated)
        latency_ms = int((time.perf_counter() - start) * 1000)
        assistant_now = int(time.time())

        assistant_msg_id = _persist_message(
            thread_id=thread_id,
            role="assistant",
            content=full_text,
            context_json=None,
            model_name=model,
            provider=provider.name,
            latency_ms=latency_ms,
            input_tokens=None,
            output_tokens=None,
            created_at=assistant_now,
        )
        _touch_thread(thread_id, assistant_now)

        yield _sse({
            "type": "done",
            "assistant_message_id": assistant_msg_id,
            "model": model,
            "provider": provider.name,
            "latency_ms": latency_ms,
        })
    except Exception as exc:
        logger.exception("run_ask_stream failed")
        yield _sse({"type": "error", "message": str(exc)})


def _sse(payload: dict[str, Any]) -> str:
    return f"data: {json.dumps(payload, ensure_ascii=False)}\n\n"


# ──────────────────────────────────────────────────────────────────
# Context retrieval
# ──────────────────────────────────────────────────────────────────


def _retrieve_context(question: str, scope: dict[str, Any]) -> dict[str, Any]:
    """Assemble the data payload the LLM will ground its answer in.

    Scope hints (explicit wins over keyword inference):
    - scope.game_id → pull that game's summary + review + session log
    - scope.since / scope.until → only games in window
    - otherwise, use keyword hints from the question
    """

    context: dict[str, Any] = {"scope": scope, "coach_visible_totals": _coach_visible_totals()}

    # Pinned game scope
    pinned_game_id = scope.get("game_id") if scope else None
    if pinned_game_id is not None:
        context["game_summaries"] = _fetch_summary_and_review(int(pinned_game_id))
        context["session_logs"] = _fetch_session_log(int(pinned_game_id))
        context["recent_reviews"] = _fetch_review_text([int(pinned_game_id)])
        context["matchup_notes"] = _fetch_matchup_for_game(int(pinned_game_id))
    else:
        # Time window or recent-N fallback
        since = scope.get("since") if scope else None
        until = scope.get("until") if scope else None
        game_ids = _pick_relevant_games(question, since=since, until=until, limit=10)
        context["game_summaries"] = [_fetch_summary_and_review(gid) for gid in game_ids]
        context["session_logs"] = [_fetch_session_log(gid) for gid in game_ids]
        context["recent_reviews"] = _fetch_review_text(game_ids)

    # Always include user concepts and signals
    context["concepts"] = _fetch_top_concepts(limit=25)
    context["signals"] = _fetch_top_signals(limit=10)
    context["objectives"] = _fetch_active_objectives()

    return context


def _coach_visible_totals() -> dict[str, int]:
    """Summary counts so the LLM can tell the user how much it 'sees'."""
    with read_core() as conn:
        games = conn.execute("SELECT COUNT(*) FROM games").fetchone()[0]
        summaries = _safe_count(conn, "SELECT COUNT(*) FROM game_summary")
        concepts = _safe_count(conn, "SELECT COUNT(*) FROM user_concept_profile")
        signals = _safe_count(conn, "SELECT COUNT(*) FROM user_signal_ranking WHERE stable = 1")
        messages = _safe_count(conn, "SELECT COUNT(*) FROM coach_chat_messages")
    return {
        "games_total": int(games),
        "games_summarized": int(summaries),
        "concepts_ranked": int(concepts),
        "signals_stable": int(signals),
        "chat_messages": int(messages),
    }


def _safe_count(conn, sql: str) -> int:
    try:
        return int(conn.execute(sql).fetchone()[0])
    except Exception:
        return 0


def _pick_relevant_games(
    question: str, since: int | None, until: int | None, limit: int
) -> list[int]:
    """Return up to `limit` recent game_ids, optionally filtered by time window."""
    q_lower = question.lower()
    # Champion mention boosts — find any champion name the user has played.
    with read_core() as conn:
        champions = [
            r["champion_name"]
            for r in conn.execute(
                "SELECT DISTINCT champion_name FROM games WHERE champion_name IS NOT NULL"
            ).fetchall()
            if r["champion_name"]
        ]
        matching_champions = [c for c in champions if c and c.lower() in q_lower]

        if matching_champions:
            params = tuple(matching_champions)
            placeholders = ",".join("?" * len(params))
            rows = conn.execute(
                f"""
                SELECT id FROM games
                WHERE champion_name IN ({placeholders})
                ORDER BY id DESC LIMIT ?
                """,
                (*params, limit),
            ).fetchall()
            if rows:
                return [int(r["id"]) for r in rows]

        # Time window
        if since is not None or until is not None:
            clauses = []
            params_list: list[Any] = []
            if since is not None:
                clauses.append("COALESCE(timestamp, 0) >= ?")
                params_list.append(since)
            if until is not None:
                clauses.append("COALESCE(timestamp, 9999999999) <= ?")
                params_list.append(until)
            where = " AND ".join(clauses) if clauses else "1=1"
            params_list.append(limit)
            rows = conn.execute(
                f"SELECT id FROM games WHERE {where} ORDER BY id DESC LIMIT ?",
                tuple(params_list),
            ).fetchall()
            return [int(r["id"]) for r in rows]

        # Default: most recent N
        rows = conn.execute(
            "SELECT id FROM games ORDER BY id DESC LIMIT ?", (limit,)
        ).fetchall()
        return [int(r["id"]) for r in rows]


def _fetch_summary_and_review(game_id: int) -> dict[str, Any]:
    with read_core() as conn:
        row = conn.execute(
            "SELECT * FROM games WHERE id = ?", (game_id,)
        ).fetchone()
        if row is None:
            return {"game_id": game_id, "error": "not_found"}
        game = dict(row)

        summary_row = conn.execute(
            "SELECT compacted_json FROM game_summary WHERE game_id = ?", (game_id,)
        ).fetchone()

    out = {
        "game_id": game_id,
        "champion": game.get("champion_name"),
        "win": bool(game.get("win")),
        "kda": [game.get("kills"), game.get("deaths"), game.get("assists")],
        "duration_s": game.get("game_duration"),
        "patch": game.get("queue_type"),
    }
    if summary_row and summary_row["compacted_json"]:
        try:
            out["compacted"] = json.loads(summary_row["compacted_json"])
        except Exception:
            pass
    return out


def _fetch_session_log(game_id: int) -> dict[str, Any]:
    with read_core() as conn:
        row = conn.execute(
            "SELECT mental_rating, improvement_note, rule_broken, date "
            "FROM session_log WHERE game_id = ? ORDER BY id DESC LIMIT 1",
            (game_id,),
        ).fetchone()
    return dict(row) if row else {"game_id": game_id, "empty": True}


def _fetch_review_text(game_ids: list[int]) -> list[dict[str, Any]]:
    if not game_ids:
        return []
    placeholders = ",".join("?" * len(game_ids))
    with read_core() as conn:
        rows = conn.execute(
            f"""
            SELECT id, champion_name, win, mistakes, went_well, focus_next,
                   review_notes, spotted_problems
            FROM games WHERE id IN ({placeholders})
            ORDER BY id DESC
            """,
            tuple(game_ids),
        ).fetchall()
    result = []
    for r in rows:
        d = dict(r)
        # Filter out empty reviews to save tokens
        if any(d.get(k) for k in ("mistakes", "went_well", "focus_next", "review_notes", "spotted_problems")):
            result.append(d)
    return result


def _fetch_matchup_for_game(game_id: int) -> list[dict[str, Any]]:
    with read_core() as conn:
        game = conn.execute(
            "SELECT champion_name FROM games WHERE id = ?", (game_id,)
        ).fetchone()
        if not game or not game["champion_name"]:
            return []
        rows = conn.execute(
            "SELECT champion, enemy, note FROM matchup_notes WHERE champion = ? ORDER BY id DESC LIMIT 5",
            (game["champion_name"],),
        ).fetchall()
    return [dict(r) for r in rows if r["note"]]


def _fetch_top_concepts(limit: int) -> list[dict[str, Any]]:
    with read_core() as conn:
        if not _table_exists(conn, "user_concept_profile"):
            return []
        rows = conn.execute(
            "SELECT concept_canonical, frequency, positive_count, negative_count, "
            "neutral_count, win_correlation, rank "
            "FROM user_concept_profile ORDER BY rank ASC LIMIT ?",
            (limit,),
        ).fetchall()
    return [dict(r) for r in rows]


def _fetch_top_signals(limit: int) -> list[dict[str, Any]]:
    with read_core() as conn:
        if not _table_exists(conn, "user_signal_ranking"):
            return []
        rows = conn.execute(
            "SELECT feature_name, rank, spearman_rho, partial_rho_mental_controlled, "
            "user_baseline_win_avg, user_baseline_loss_avg, stable "
            "FROM user_signal_ranking WHERE stable = 1 ORDER BY rank ASC LIMIT ?",
            (limit,),
        ).fetchall()
    return [dict(r) for r in rows]


def _fetch_active_objectives() -> list[dict[str, Any]]:
    with read_core() as conn:
        if not _table_exists(conn, "objectives"):
            return []
        rows = conn.execute(
            "SELECT id, title, score, game_count FROM objectives WHERE active = 1"
        ).fetchall() if _has_column(conn, "objectives", "active") else conn.execute(
            "SELECT * FROM objectives"
        ).fetchall()
    return [dict(r) for r in rows]


def _table_exists(conn, name: str) -> bool:
    r = conn.execute(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name = ?", (name,)
    ).fetchone()
    return r is not None


def _has_column(conn, table: str, column: str) -> bool:
    try:
        rows = conn.execute(f"PRAGMA table_info({table})").fetchall()
        return any(r["name"] == column for r in rows)
    except Exception:
        return False


def _pick_model(cfg) -> str:
    if cfg.provider == "ollama":
        return cfg.ollama.model
    if cfg.provider == "google_ai":
        return cfg.google_ai.model
    return cfg.openrouter.model


# ──────────────────────────────────────────────────────────────────
# Chat persistence
# ──────────────────────────────────────────────────────────────────


def _create_thread(scope: dict[str, Any] | None, now: int, title: str | None) -> int:
    with write_coach() as conn:
        cursor = conn.execute(
            """
            INSERT INTO coach_chat_threads (title, scope_json, created_at, updated_at)
            VALUES (?, ?, ?, ?)
            """,
            (title, json.dumps(scope) if scope else None, now, now),
        )
        return int(cursor.lastrowid)


def _load_thread_scope(thread_id: int) -> dict[str, Any] | None:
    with read_core() as conn:
        row = conn.execute(
            "SELECT scope_json FROM coach_chat_threads WHERE id = ?", (thread_id,)
        ).fetchone()
        if row is None or not row["scope_json"]:
            return None
        try:
            return json.loads(row["scope_json"])
        except Exception:
            return None


def _load_recent_messages(thread_id: int, limit: int) -> list[dict[str, Any]]:
    """Return the most recent N messages in chronological order."""
    with read_core() as conn:
        rows = conn.execute(
            "SELECT role, content FROM coach_chat_messages "
            "WHERE thread_id = ? ORDER BY id DESC LIMIT ?",
            (thread_id, limit),
        ).fetchall()
    return [{"role": r["role"], "content": r["content"]} for r in reversed(rows)]


def _persist_message(
    *,
    thread_id: int,
    role: str,
    content: str,
    context_json: str | None,
    model_name: str | None,
    provider: str | None,
    latency_ms: int | None,
    input_tokens: int | None,
    output_tokens: int | None,
    created_at: int,
) -> int:
    with write_coach() as conn:
        cursor = conn.execute(
            """
            INSERT INTO coach_chat_messages
                (thread_id, role, content, context_json, model_name, provider,
                 latency_ms, input_tokens, output_tokens, created_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                thread_id,
                role,
                content,
                context_json,
                model_name,
                provider,
                latency_ms,
                input_tokens,
                output_tokens,
                created_at,
            ),
        )
        return int(cursor.lastrowid)


def _touch_thread(thread_id: int, ts: int) -> None:
    with write_coach() as conn:
        conn.execute(
            "UPDATE coach_chat_threads SET updated_at = ? WHERE id = ?",
            (ts, thread_id),
        )


def title_from_question(question: str) -> str:
    trimmed = question.strip()
    if len(trimmed) <= 60:
        return trimmed
    return trimmed[:57] + "..."


# ──────────────────────────────────────────────────────────────────
# Thread read API (for listing past conversations)
# ──────────────────────────────────────────────────────────────────


def load_thread(thread_id: int) -> dict[str, Any] | None:
    with read_core() as conn:
        thread_row = conn.execute(
            "SELECT id, title, scope_json, created_at, updated_at FROM coach_chat_threads WHERE id = ?",
            (thread_id,),
        ).fetchone()
        if thread_row is None:
            return None
        message_rows = conn.execute(
            """
            SELECT id, thread_id, role, content, model_name, provider, latency_ms, created_at
            FROM coach_chat_messages WHERE thread_id = ? ORDER BY id ASC
            """,
            (thread_id,),
        ).fetchall()
    scope = None
    if thread_row["scope_json"]:
        try:
            scope = json.loads(thread_row["scope_json"])
        except Exception:
            scope = None
    return {
        "id": int(thread_row["id"]),
        "title": thread_row["title"],
        "scope": scope,
        "created_at": int(thread_row["created_at"]),
        "updated_at": int(thread_row["updated_at"]),
        "messages": [
            {
                "id": int(m["id"]),
                "thread_id": int(m["thread_id"]),
                "role": m["role"],
                "content": m["content"],
                "model": m["model_name"],
                "provider": m["provider"],
                "latency_ms": m["latency_ms"],
                "created_at": int(m["created_at"]),
            }
            for m in message_rows
        ],
    }


def list_threads(limit: int = 50) -> list[dict[str, Any]]:
    with read_core() as conn:
        rows = conn.execute(
            "SELECT id, title, scope_json, created_at, updated_at "
            "FROM coach_chat_threads ORDER BY updated_at DESC LIMIT ?",
            (limit,),
        ).fetchall()
    return [
        {
            "id": int(r["id"]),
            "title": r["title"],
            "scope": json.loads(r["scope_json"]) if r["scope_json"] else None,
            "created_at": int(r["created_at"]),
            "updated_at": int(r["updated_at"]),
        }
        for r in rows
    ]

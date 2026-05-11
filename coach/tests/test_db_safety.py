import pytest


def test_write_coach_blocks_core_table_writes(isolated_user_data):
    _, _, db = isolated_user_data

    with pytest.raises(db.AllowlistViolation):
        with db.write_coach() as conn:
            conn.execute("INSERT INTO games (game_id) VALUES (?)", (123,))


def test_write_coach_allows_coach_table_writes(isolated_user_data):
    _, _, db = isolated_user_data

    with db.write_coach() as conn:
        conn.execute(
            """
            INSERT INTO coach_sessions
                (id, mode, scope_json, context_json, response_text, model_name, provider, created_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (1, "ask", "{}", "{}", "ok", "test-model", "test-provider", 1000),
        )

    with db.write_coach() as conn:
        row = conn.execute("SELECT response_text FROM coach_sessions WHERE id = ?", (1,)).fetchone()

    assert row["response_text"] == "ok"

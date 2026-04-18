-- Chat history persisted across app restarts so the coach has continuous
-- memory. One thread per "conversation" with many messages.

CREATE TABLE IF NOT EXISTS coach_chat_threads (
    id INTEGER PRIMARY KEY,
    title TEXT,
    scope_json TEXT,        -- optional scope pinned at thread creation (e.g. {"game_id": 123})
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS coach_chat_messages (
    id INTEGER PRIMARY KEY,
    thread_id INTEGER NOT NULL,
    role TEXT NOT NULL,     -- 'user' | 'assistant'
    content TEXT NOT NULL,
    context_json TEXT,      -- what data was injected for this turn (debugging)
    model_name TEXT,
    provider TEXT,
    latency_ms INTEGER,
    input_tokens INTEGER,
    output_tokens INTEGER,
    created_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_coach_chat_messages_thread ON coach_chat_messages(thread_id, id);

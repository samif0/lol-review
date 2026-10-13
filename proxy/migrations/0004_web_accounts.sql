-- Web accounts v1: cookie sessions for revu.lol, attempt counting for web
-- sign-in codes, and a generic identities table (email backfilled) so social
-- providers can be added later without a users-table rebuild.
ALTER TABLE sessions ADD COLUMN kind TEXT NOT NULL DEFAULT 'desktop';
CREATE INDEX IF NOT EXISTS idx_sessions_user_kind ON sessions (user_id, kind);

ALTER TABLE login_requests ADD COLUMN attempts INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS identities (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id      INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    provider     TEXT    NOT NULL,          -- 'email' in v1; later 'google' | 'riot'
    subject      TEXT    NOT NULL,          -- email: lower(email); oidc: verified sub
    email        TEXT    COLLATE NOCASE,
    display_name TEXT,
    created_at   INTEGER NOT NULL,
    last_used_at INTEGER,
    UNIQUE (provider, subject)
);
CREATE INDEX IF NOT EXISTS idx_identities_user ON identities (user_id);
-- One email identity per user. A partial index (not a table constraint) so it
-- can be dropped later without a rebuild; several riot identities may link.
CREATE UNIQUE INDEX IF NOT EXISTS idx_identities_one_email_per_user
    ON identities (user_id) WHERE provider = 'email';

INSERT OR IGNORE INTO identities (user_id, provider, subject, email, created_at)
    SELECT id, 'email', lower(email), email, created_at FROM users;

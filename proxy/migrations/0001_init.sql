-- Users who have signed up via magic-link.
CREATE TABLE users (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    email            TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    created_at       INTEGER NOT NULL,
    invite_code_used TEXT
);
CREATE INDEX idx_users_email ON users (email);

-- Invite codes that gate signup. Single-use; consumed when a user signs up
-- with a given code.
CREATE TABLE invite_codes (
    code       TEXT    PRIMARY KEY,
    created_at INTEGER NOT NULL,
    used_by    INTEGER,               -- user_id when consumed, else NULL
    used_at    INTEGER
);

-- Active sessions. Session token is the opaque bearer string the desktop app
-- sends as Authorization: Bearer <token>. Lookup is by token hash to avoid
-- storing raw tokens at rest.
CREATE TABLE sessions (
    token_hash TEXT    PRIMARY KEY,   -- sha256(token) hex
    user_id    INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,      -- unix seconds
    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
);
CREATE INDEX idx_sessions_user ON sessions (user_id);
CREATE INDEX idx_sessions_expires ON sessions (expires_at);

-- Short-lived one-time codes issued during signup/login. User receives the
-- code via email, pastes into the desktop app, app exchanges at /auth/verify
-- for a session token.
CREATE TABLE login_requests (
    code       TEXT    PRIMARY KEY,
    email      TEXT    NOT NULL COLLATE NOCASE,
    purpose    TEXT    NOT NULL,      -- 'signup' | 'login'
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,      -- unix seconds (~10 min)
    consumed   INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_login_requests_email ON login_requests (email);
CREATE INDEX idx_login_requests_expires ON login_requests (expires_at);

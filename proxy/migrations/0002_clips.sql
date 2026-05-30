-- Shared clips. A user uploads a local clip file; we store the bytes in R2
-- (key = r2_key) and the metadata here. The id is the public slug that appears
-- in revu.lol/<id>. Clips auto-expire after 30 days; a daily cron purges rows
-- where expires_at has passed (and the matching R2 object).
CREATE TABLE clips (
    id           TEXT    PRIMARY KEY,        -- short base62 public slug (revu.lol/<id>)
    user_id      INTEGER NOT NULL,           -- uploader; clip is theirs to delete
    r2_key       TEXT    NOT NULL,           -- object key in the CLIPS R2 bucket
    content_type TEXT    NOT NULL,           -- 'video/mp4' | 'video/webm'
    size_bytes   INTEGER NOT NULL,
    duration_s   INTEGER,                    -- clip length in seconds, if known
    title        TEXT,                       -- uploader-typed caption (no account data)
    champion     TEXT,                       -- uploader-typed champion tag
    created_at   INTEGER NOT NULL,           -- unix seconds
    expires_at   INTEGER NOT NULL,           -- unix seconds; created_at + 30 days
    view_count   INTEGER NOT NULL DEFAULT 0,
    status       TEXT    NOT NULL DEFAULT 'ready',
    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
);
CREATE INDEX idx_clips_user ON clips (user_id);
CREATE INDEX idx_clips_expires ON clips (expires_at);

-- Revu 4.1: full-game (VOD) shares. VOD rows live in `clips` with kind = 'vod'
-- so the purge cron, owner delete, account delete and the storage budget treat
-- every stored object the same way. Existing rows become kind 'clip'.
ALTER TABLE clips ADD COLUMN kind TEXT NOT NULL DEFAULT 'clip';       -- 'clip' | 'vod'
ALTER TABLE clips ADD COLUMN part_puts INTEGER NOT NULL DEFAULT 0;    -- part PUT attempts, cap 2*part_count+4
ALTER TABLE clips ADD COLUMN quality TEXT;                            -- vod only: '1080p' | '720p'
ALTER TABLE clips ADD COLUMN play_day TEXT;                           -- vod only: UTC 'YYYY-MM-DD' of play_count
ALTER TABLE clips ADD COLUMN play_count INTEGER NOT NULL DEFAULT 0;   -- vod only: counted plays on play_day
CREATE INDEX IF NOT EXISTS idx_clips_kind_status_expires ON clips (kind, status, expires_at);
CREATE INDEX IF NOT EXISTS idx_clips_user_kind_status ON clips (user_id, kind, status);

-- Accounts allowed to start VOD shares. Filled by hand at deploy time; never by code.
CREATE TABLE IF NOT EXISTS vod_sharers (
    user_id  INTEGER PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    added_at INTEGER NOT NULL
);

-- Per-user VOD share starts per UTC day. Counts only; pruned after 7 days.
CREATE TABLE IF NOT EXISTS vod_share_usage (
    user_id INTEGER NOT NULL,
    day     TEXT    NOT NULL,                 -- 'YYYY-MM-DD' (UTC)
    starts  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, day)
);

-- Per-user multipart part PUTs per UTC day, both kinds together. Survives row
-- deletes so delete-and-restart can't reset it. Counts only; pruned after 7 days.
CREATE TABLE IF NOT EXISTS upload_part_usage (
    user_id INTEGER NOT NULL,
    day     TEXT    NOT NULL,                 -- 'YYYY-MM-DD' (UTC)
    puts    INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, day)
);

-- Markers + non-personal game meta (+ notes only when the sharer opted in).
CREATE TABLE IF NOT EXISTS vod_share_meta (
    clip_id    TEXT    PRIMARY KEY REFERENCES clips (id) ON DELETE CASCADE,
    meta_json  TEXT    NOT NULL,              -- normalized markers document v1, <= 64 KB
    has_notes  INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL
);

-- R2 objects whose delete failed after their owning row had to go (account
-- delete). No foreign key: a tombstone outlives its user. Counted in the
-- storage budget and retried by the hourly purge.
CREATE TABLE IF NOT EXISTS storage_tombstones (
    r2_key     TEXT    PRIMARY KEY,
    upload_id  TEXT,
    size_bytes INTEGER NOT NULL,
    kind       TEXT    NOT NULL DEFAULT 'clip',
    created_at INTEGER NOT NULL
);

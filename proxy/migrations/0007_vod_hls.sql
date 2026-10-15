-- Revu 4.4: full-game shares that play while they upload (HLS: 2 s fMP4
-- segments, one R2 object each, under vods/<id>/<seq>.m4s). Additive only:
-- every existing row keeps format NULL (one MP4 object, or a clip).
ALTER TABLE clips ADD COLUMN format TEXT;                           -- vod only: NULL = one MP4 object, 'hls' = segments
ALTER TABLE clips ADD COLUMN hls_bytes INTEGER NOT NULL DEFAULT 0;  -- hls only: init bytes + bytes of every segment row, pending or committed

-- One row per HLS share (1:1 with clips), so SELECT * on clips never drags the
-- segment state along.
CREATE TABLE IF NOT EXISTS vod_hls (
    clip_id        TEXT    PRIMARY KEY REFERENCES clips (id) ON DELETE CASCADE,
    segments       INTEGER NOT NULL DEFAULT 0,   -- committed contiguously from seq 0: the playlist lists 0..segments-1
    media_ms       INTEGER NOT NULL DEFAULT 0,   -- EXTINF sum of those segments
    max_seq        INTEGER NOT NULL DEFAULT -1,  -- highest seq a PUT ever claimed: the keys every cleanup deletes
    runs           INTEGER NOT NULL DEFAULT 0,   -- runs stored, k = 0..runs-1
    last_commit_ms INTEGER,                      -- unix ms of the last segment commit, NULL before the first
    ingest_milli   INTEGER,                      -- EWMA of committed media per wall time, x1000 (9120 = 9.12x); NULL before 2 commits
    paused_at      INTEGER,                      -- unix s of the first pause still in effect, NULL when not paused
    paused_reason  TEXT,                         -- 'game' while paused
    ended          INTEGER NOT NULL DEFAULT 0,   -- 1 once ended (/end or the cron): the playlist gets ENDLIST
    truncated      INTEGER NOT NULL DEFAULT 0    -- 1 when it ended before the whole game was uploaded
);

-- One row per run: an encode that starts at segment from_seq with its own init
-- (ftyp + moov). Run 0 starts at 0; a resume at the edge is run k >= 1.
CREATE TABLE IF NOT EXISTS vod_hls_runs (
    clip_id  TEXT    NOT NULL REFERENCES clips (id) ON DELETE CASCADE,
    k        INTEGER NOT NULL,                   -- 0..7
    from_seq INTEGER NOT NULL,                   -- first segment of this run
    init     BLOB    NOT NULL,                   -- the init segment, 16 KiB at most
    quality  TEXT    NOT NULL,                   -- '1080p60', '720p60', ...
    PRIMARY KEY (clip_id, k)
) WITHOUT ROWID;

-- One row per segment slot a PUT claimed. A row is pending (committed = 0)
-- from its claim until its object is stored; bytes count in clips.hls_bytes
-- either way, so an aborted PUT and its retry are counted once.
CREATE TABLE IF NOT EXISTS vod_hls_segments (
    clip_id     TEXT    NOT NULL REFERENCES clips (id) ON DELETE CASCADE,
    seq         INTEGER NOT NULL,                -- 0..clips.part_count-1
    duration_ms INTEGER NOT NULL,                -- EXTINF in milliseconds, 1..6499
    bytes       INTEGER NOT NULL,                -- body length of the latest claim
    committed   INTEGER NOT NULL DEFAULT 0,      -- 1 once the object is stored; never changes after
    PRIMARY KEY (clip_id, seq)
) WITHOUT ROWID;

-- A prefix tombstone (r2_key 'vods/<id>/') stands for the segment keys
-- vods/<id>/<seq>.m4s with seq 0..key_count-1, and is not purged before
-- not_before (so PUTs still in flight at a delete have settled). Both are NULL
-- for every single-object tombstone.
ALTER TABLE storage_tombstones ADD COLUMN key_count INTEGER;
ALTER TABLE storage_tombstones ADD COLUMN not_before INTEGER;

-- Operator switches read through a 30 s per-isolate cache: 'vod_serving' and
-- 'vod_hls' set to '0' turn VOD playback / new HLS starts off without a deploy.
-- No row means the wrangler.toml var decides. Written by hand only.
CREATE TABLE IF NOT EXISTS ops_flags (
    name  TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- Revu 3.14.0: multipart clip uploads (status 'uploading' rows), narrated flag,
-- and clip transcripts (C2 JSON + generated VTT). transcribe_usage holds the
-- per-user daily Workers AI audio budget; user_id 0 is the global row.
ALTER TABLE clips ADD COLUMN upload_id TEXT;
ALTER TABLE clips ADD COLUMN part_size INTEGER;
ALTER TABLE clips ADD COLUMN part_count INTEGER;
ALTER TABLE clips ADD COLUMN last_activity_at INTEGER;
ALTER TABLE clips ADD COLUMN narrated INTEGER NOT NULL DEFAULT 0;
ALTER TABLE clips ADD COLUMN has_transcript INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS idx_clips_status_expires ON clips(status, expires_at);
CREATE INDEX IF NOT EXISTS idx_clips_user_status ON clips(user_id, status);
CREATE TABLE IF NOT EXISTS clip_transcripts (clip_id TEXT PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE, language TEXT, segments_json TEXT NOT NULL, vtt TEXT NOT NULL, segment_count INTEGER NOT NULL, created_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS transcribe_usage (user_id INTEGER NOT NULL, day TEXT NOT NULL, audio_seconds INTEGER NOT NULL DEFAULT 0, requests INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (user_id, day));

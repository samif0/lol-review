-- Coach rebuild migrations (additive only).
-- These tables are new and do not collide with the orphaned legacy coach_* tables.
-- See COACH_PLAN.md §4.

-- Phase 1
CREATE TABLE IF NOT EXISTS game_summary (
    game_id INTEGER PRIMARY KEY,
    compacted_json TEXT NOT NULL,
    win_probability_timeline_json TEXT,
    key_events_json TEXT,
    summary_version INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    token_count INTEGER
);

-- Phase 2
CREATE TABLE IF NOT EXISTS review_concepts (
    id INTEGER PRIMARY KEY,
    game_id INTEGER NOT NULL,
    source_field TEXT NOT NULL,
    concept_raw TEXT NOT NULL,
    concept_canonical TEXT,
    polarity TEXT NOT NULL,
    span TEXT NOT NULL,
    cluster_id INTEGER,
    created_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_review_concepts_game ON review_concepts(game_id);
CREATE INDEX IF NOT EXISTS idx_review_concepts_cluster ON review_concepts(cluster_id);

CREATE TABLE IF NOT EXISTS user_concept_profile (
    concept_canonical TEXT PRIMARY KEY,
    frequency INTEGER NOT NULL,
    recency_weighted_frequency REAL NOT NULL,
    positive_count INTEGER NOT NULL,
    negative_count INTEGER NOT NULL,
    neutral_count INTEGER NOT NULL,
    win_correlation REAL,
    last_seen_at INTEGER NOT NULL,
    rank INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

-- Phase 3
CREATE TABLE IF NOT EXISTS feature_values (
    game_id INTEGER NOT NULL,
    feature_name TEXT NOT NULL,
    value REAL,
    PRIMARY KEY (game_id, feature_name)
);

CREATE INDEX IF NOT EXISTS idx_feature_values_name ON feature_values(feature_name);

CREATE TABLE IF NOT EXISTS user_signal_ranking (
    feature_name TEXT PRIMARY KEY,
    spearman_rho REAL NOT NULL,
    partial_rho_mental_controlled REAL,
    ci_low REAL NOT NULL,
    ci_high REAL NOT NULL,
    sample_size INTEGER NOT NULL,
    stable INTEGER NOT NULL,
    drift_flag INTEGER NOT NULL,
    user_baseline_win_avg REAL,
    user_baseline_loss_avg REAL,
    rank INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

-- Phase 4
CREATE TABLE IF NOT EXISTS clip_frame_descriptions (
    bookmark_id INTEGER NOT NULL,
    frame_timestamp_ms INTEGER NOT NULL,
    frame_path TEXT NOT NULL,
    description_text TEXT NOT NULL,
    model_name TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (bookmark_id, frame_timestamp_ms)
);

-- Phase 5 — coach_sessions is also created in db.py bootstrap for safety
CREATE TABLE IF NOT EXISTS coach_sessions (
    id INTEGER PRIMARY KEY,
    mode TEXT NOT NULL,
    scope_json TEXT NOT NULL,
    context_json TEXT NOT NULL,
    response_text TEXT NOT NULL,
    response_json TEXT,
    model_name TEXT NOT NULL,
    provider TEXT NOT NULL,
    latency_ms INTEGER,
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS coach_response_edits (
    coach_session_id INTEGER PRIMARY KEY,
    edited_text TEXT NOT NULL,
    edit_distance INTEGER,
    created_at INTEGER NOT NULL
);

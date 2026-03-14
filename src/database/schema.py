"""Database schema definitions and default data."""

CREATE_GAMES_TABLE = """
CREATE TABLE IF NOT EXISTS games (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    game_id         INTEGER UNIQUE,
    timestamp       INTEGER,
    date_played     TEXT,
    game_duration   INTEGER,
    game_mode       TEXT,
    game_type       TEXT,
    queue_type      TEXT,

    summoner_name   TEXT,
    champion_name   TEXT,
    champion_id     INTEGER,
    team_id         INTEGER,
    position        TEXT,
    role            TEXT,

    win             INTEGER,

    kills           INTEGER,
    deaths          INTEGER,
    assists         INTEGER,
    kda_ratio       REAL,
    largest_killing_spree   INTEGER,
    largest_multi_kill      INTEGER,
    double_kills    INTEGER,
    triple_kills    INTEGER,
    quadra_kills    INTEGER,
    penta_kills     INTEGER,
    first_blood     INTEGER,

    total_damage_dealt              INTEGER,
    total_damage_to_champions       INTEGER,
    physical_damage_to_champions    INTEGER,
    magic_damage_to_champions       INTEGER,
    true_damage_to_champions        INTEGER,
    total_damage_taken              INTEGER,
    damage_self_mitigated           INTEGER,
    largest_critical_strike         INTEGER,

    gold_earned     INTEGER,
    gold_spent      INTEGER,
    total_minions_killed    INTEGER,
    neutral_minions_killed  INTEGER,
    cs_total        INTEGER,
    cs_per_min      REAL,

    vision_score    INTEGER,
    wards_placed    INTEGER,
    wards_killed    INTEGER,
    control_wards_purchased INTEGER,

    turret_kills    INTEGER,
    inhibitor_kills INTEGER,
    dragon_kills    INTEGER,
    baron_kills     INTEGER,
    rift_herald_kills INTEGER,

    total_heal      INTEGER,
    total_heals_on_teammates    INTEGER,
    total_damage_shielded_on_teammates INTEGER,
    total_time_cc_dealt         INTEGER,
    time_ccing_others           INTEGER,

    spell1_casts    INTEGER,
    spell2_casts    INTEGER,
    spell3_casts    INTEGER,
    spell4_casts    INTEGER,
    summoner1_id    INTEGER,
    summoner2_id    INTEGER,
    items           TEXT,

    champ_level     INTEGER,
    team_kills      INTEGER,
    kill_participation REAL,

    raw_stats       TEXT,

    -- Review fields
    review_notes    TEXT DEFAULT '',
    rating          INTEGER DEFAULT 0,
    tags            TEXT DEFAULT '[]',
    goals_met       TEXT DEFAULT '[]',
    mistakes        TEXT DEFAULT '',
    went_well       TEXT DEFAULT '',
    focus_next      TEXT DEFAULT ''
);
"""

CREATE_TAGS_TABLE = """
CREATE TABLE IF NOT EXISTS tags (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    name    TEXT UNIQUE NOT NULL,
    color   TEXT DEFAULT '#3b82f6'
);
"""

CREATE_SESSION_LOG_TABLE = """
CREATE TABLE IF NOT EXISTS session_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    date            TEXT NOT NULL,
    game_id         INTEGER,
    champion_name   TEXT,
    win             INTEGER,
    mental_rating   INTEGER DEFAULT 5,
    improvement_note TEXT DEFAULT '',
    rule_broken     INTEGER DEFAULT 0,
    timestamp       INTEGER,
    FOREIGN KEY (game_id) REFERENCES games(game_id)
);
"""

CREATE_PERSISTENT_NOTES_TABLE = """
CREATE TABLE IF NOT EXISTS persistent_notes (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    content TEXT DEFAULT '',
    updated_at INTEGER
);
"""

CREATE_VOD_FILES_TABLE = """
CREATE TABLE IF NOT EXISTS vod_files (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    game_id     INTEGER UNIQUE NOT NULL,
    file_path   TEXT NOT NULL,
    file_size   INTEGER DEFAULT 0,
    duration_s  INTEGER DEFAULT 0,
    matched_at  INTEGER,
    FOREIGN KEY (game_id) REFERENCES games(game_id)
);
"""

CREATE_GAME_EVENTS_TABLE = """
CREATE TABLE IF NOT EXISTS game_events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    game_id     INTEGER NOT NULL,
    event_type  TEXT NOT NULL,
    game_time_s INTEGER NOT NULL,
    details     TEXT DEFAULT '{}',
    FOREIGN KEY (game_id) REFERENCES games(game_id)
);
"""

CREATE_GAME_EVENTS_INDEX = """
CREATE INDEX IF NOT EXISTS idx_game_events_game_id
ON game_events (game_id, game_time_s);
"""

CREATE_VOD_BOOKMARKS_TABLE = """
CREATE TABLE IF NOT EXISTS vod_bookmarks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    game_id     INTEGER NOT NULL,
    game_time_s INTEGER NOT NULL,
    note        TEXT DEFAULT '',
    tags        TEXT DEFAULT '[]',
    clip_start_s INTEGER,
    clip_end_s   INTEGER,
    clip_path    TEXT DEFAULT '',
    created_at  INTEGER,
    FOREIGN KEY (game_id) REFERENCES games(game_id)
);
"""

# Migration statements for existing databases (added via ALTER TABLE)
MIGRATE_BOOKMARKS_CLIP_COLUMNS = [
    "ALTER TABLE vod_bookmarks ADD COLUMN clip_start_s INTEGER",
    "ALTER TABLE vod_bookmarks ADD COLUMN clip_end_s INTEGER",
    "ALTER TABLE vod_bookmarks ADD COLUMN clip_path TEXT DEFAULT ''",
]

MIGRATE_SESSION_LOG_MENTAL = [
    "ALTER TABLE session_log ADD COLUMN pregame_intention TEXT DEFAULT ''",
    "ALTER TABLE session_log ADD COLUMN mental_handled TEXT DEFAULT ''",
]

CREATE_OBJECTIVES_TABLE = """
CREATE TABLE IF NOT EXISTS objectives (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    title               TEXT NOT NULL,
    skill_area          TEXT DEFAULT '',
    type                TEXT DEFAULT 'primary',
    completion_criteria TEXT DEFAULT '',
    description         TEXT DEFAULT '',
    status              TEXT DEFAULT 'active',
    score               INTEGER DEFAULT 0,
    game_count          INTEGER DEFAULT 0,
    created_at          INTEGER,
    completed_at        INTEGER
);
"""

CREATE_GAME_OBJECTIVES_TABLE = """
CREATE TABLE IF NOT EXISTS game_objectives (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    game_id         INTEGER NOT NULL,
    objective_id    INTEGER NOT NULL,
    practiced       INTEGER DEFAULT 1,
    execution_note  TEXT DEFAULT '',
    FOREIGN KEY (game_id) REFERENCES games(game_id),
    FOREIGN KEY (objective_id) REFERENCES objectives(id),
    UNIQUE(game_id, objective_id)
);
"""

CREATE_CONCEPT_TAGS_TABLE = """
CREATE TABLE IF NOT EXISTS concept_tags (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    name     TEXT UNIQUE NOT NULL,
    polarity TEXT DEFAULT 'neutral',
    color    TEXT DEFAULT '#3b82f6'
);
"""

CREATE_GAME_CONCEPT_TAGS_TABLE = """
CREATE TABLE IF NOT EXISTS game_concept_tags (
    game_id INTEGER NOT NULL,
    tag_id  INTEGER NOT NULL,
    PRIMARY KEY (game_id, tag_id),
    FOREIGN KEY (game_id) REFERENCES games(game_id),
    FOREIGN KEY (tag_id) REFERENCES concept_tags(id)
);
"""

DEFAULT_CONCEPT_TAGS = [
    ("Dominated lane",    "positive", "#22c55e"),
    ("Won teamfight",     "positive", "#22c55e"),
    ("Good roam",         "positive", "#22c55e"),
    ("Objective control", "positive", "#22c55e"),
    ("Survived early",    "positive", "#22c55e"),
    ("Caught out",        "negative", "#ef4444"),
    ("Bad trade",         "negative", "#ef4444"),
    ("Poor macro",        "negative", "#ef4444"),
    ("Tilted",            "negative", "#ef4444"),
    ("Overextended",      "negative", "#ef4444"),
    ("Even game",         "neutral",  "#3b82f6"),
    ("Team diff",         "neutral",  "#3b82f6"),
    ("Passive game",      "neutral",  "#3b82f6"),
]

# Pre-populate some useful default tags
DEFAULT_TAGS = [
    ("Tilted", "#ef4444"),
    ("Stomped Lane", "#22c55e"),
    ("Got Carried", "#eab308"),
    ("Comeback", "#8b5cf6"),
    ("Threw Lead", "#f97316"),
    ("Good Macro", "#06b6d4"),
    ("Bad Draft", "#ec4899"),
    ("Clean Game", "#10b981"),
    ("Tough Matchup", "#f59e0b"),
    ("Team Diff", "#6366f1"),
]

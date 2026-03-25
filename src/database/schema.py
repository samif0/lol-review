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
    focus_next      TEXT DEFAULT '',
    spotted_problems TEXT DEFAULT ''
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

CREATE_RULES_TABLE = """
CREATE TABLE IF NOT EXISTS rules (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT NOT NULL,
    description     TEXT DEFAULT '',
    rule_type       TEXT DEFAULT 'custom',
    condition_value TEXT DEFAULT '',
    is_active       INTEGER DEFAULT 1,
    created_at      INTEGER
);
"""

CREATE_DERIVED_EVENT_DEFINITIONS_TABLE = """
CREATE TABLE IF NOT EXISTS derived_event_definitions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT UNIQUE NOT NULL,
    source_types    TEXT NOT NULL,
    min_count       INTEGER NOT NULL,
    window_seconds  INTEGER NOT NULL,
    color           TEXT DEFAULT '#ff6b6b',
    is_default      INTEGER DEFAULT 0,
    created_at      INTEGER
);
"""

CREATE_DERIVED_EVENT_INSTANCES_TABLE = """
CREATE TABLE IF NOT EXISTS derived_event_instances (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    game_id         INTEGER NOT NULL,
    definition_id   INTEGER NOT NULL,
    start_time_s    INTEGER NOT NULL,
    end_time_s      INTEGER NOT NULL,
    event_count     INTEGER NOT NULL,
    source_event_ids TEXT DEFAULT '[]',
    FOREIGN KEY (game_id) REFERENCES games(game_id),
    FOREIGN KEY (definition_id) REFERENCES derived_event_definitions(id)
);
"""

DEFAULT_DERIVED_EVENTS = [
    ("Teamfight",         '["KILL","DEATH"]',                    3, 15, "#ff6b6b"),
    ("Skirmish",          '["KILL","DEATH"]',                    2, 10, "#ffa07a"),
    ("Objective Contest",  '["DRAGON","BARON","KILL","DEATH"]',  2, 30, "#c89b3c"),
    ("Death Streak",      '["DEATH"]',                          2, 60, "#ea5455"),
    ("Tower Dive",        '["TURRET","KILL","DEATH"]',          2, 10, "#f97316"),
]

CREATE_OBJECTIVE_PROMPTS_TABLE = """
CREATE TABLE IF NOT EXISTS objective_prompts (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    objective_id    INTEGER NOT NULL,
    question_text   TEXT NOT NULL,
    event_tag       TEXT DEFAULT '',
    answer_type     TEXT DEFAULT 'yes_no',
    sort_order      INTEGER DEFAULT 0,
    FOREIGN KEY (objective_id) REFERENCES objectives(id)
);
"""

CREATE_PROMPT_ANSWERS_TABLE = """
CREATE TABLE IF NOT EXISTS prompt_answers (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    game_id           INTEGER NOT NULL,
    prompt_id         INTEGER NOT NULL,
    event_instance_id INTEGER,
    event_time_s      INTEGER,
    answer_value      INTEGER NOT NULL,
    FOREIGN KEY (game_id) REFERENCES games(game_id),
    FOREIGN KEY (prompt_id) REFERENCES objective_prompts(id),
    UNIQUE(game_id, prompt_id, event_instance_id)
);
"""

CREATE_MATCHUP_NOTES_TABLE = """
CREATE TABLE IF NOT EXISTS matchup_notes (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    champion    TEXT NOT NULL,
    enemy       TEXT NOT NULL,
    note        TEXT DEFAULT '',
    helpful     INTEGER,
    game_id     INTEGER,
    created_at  INTEGER,
    FOREIGN KEY (game_id) REFERENCES games(game_id)
);
"""

MIGRATE_GAMES_ENEMY_LANER = [
    "ALTER TABLE games ADD COLUMN enemy_laner TEXT DEFAULT ''",
]

MIGRATE_GAMES_SPOTTED_PROBLEMS = [
    "ALTER TABLE games ADD COLUMN spotted_problems TEXT DEFAULT ''",
]

# Cognitive reappraisal fields (Gross 1998/2002; Buhle et al. 2014 meta-analysis)
MIGRATE_GAMES_REAPPRAISAL = [
    "ALTER TABLE games ADD COLUMN outside_control TEXT DEFAULT ''",
    "ALTER TABLE games ADD COLUMN within_control TEXT DEFAULT ''",
]

# Attribution tracking (Weiner 1985; Dweck 2006)
MIGRATE_GAMES_ATTRIBUTION = [
    "ALTER TABLE games ADD COLUMN attribution TEXT DEFAULT ''",
]

# Self-efficacy anchoring (Bandura 1977/1997)
MIGRATE_GAMES_SELF_EFFICACY = [
    "ALTER TABLE games ADD COLUMN personal_contribution TEXT DEFAULT ''",
]

# Pre-game mood / affect labeling (Lieberman et al. 2007)
MIGRATE_SESSION_LOG_MOOD = [
    "ALTER TABLE session_log ADD COLUMN pre_game_mood INTEGER DEFAULT 0",
]

# Session-level intentions and debriefs (Gollwitzer 1999)
CREATE_SESSIONS_TABLE = """
CREATE TABLE IF NOT EXISTS sessions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    date            TEXT UNIQUE NOT NULL,
    intention       TEXT DEFAULT '',
    debrief_rating  INTEGER DEFAULT 0,
    debrief_note    TEXT DEFAULT '',
    started_at      INTEGER,
    ended_at        INTEGER
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

CREATE_TILT_CHECKS_TABLE = """
CREATE TABLE IF NOT EXISTS tilt_checks (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    emotion           TEXT NOT NULL,
    intensity_before  INTEGER NOT NULL,
    intensity_after   INTEGER,
    reframe_thought   TEXT DEFAULT '',
    reframe_response  TEXT DEFAULT '',
    thought_type      TEXT DEFAULT '',
    cue_word          TEXT DEFAULT '',
    focus_intention   TEXT DEFAULT '',
    created_at        INTEGER
);
"""

#nullable enable

namespace Revu.Core.Data;

/// <summary>
/// All DDL statements ported verbatim from the Python schema.py.
/// SQL must remain identical for backward compatibility with existing databases.
/// </summary>
public static class Schema
{
    // ── CREATE TABLE statements ──────────────────────────────────────

    public const string CreateGamesTable = """
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
        """;

    public const string CreateSessionLogTable = """
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
        """;

    public const string CreatePersistentNotesTable = """
        CREATE TABLE IF NOT EXISTS persistent_notes (
            id      INTEGER PRIMARY KEY AUTOINCREMENT,
            content TEXT DEFAULT '',
            updated_at INTEGER
        );
        """;

    public const string CreateVodFilesTable = """
        CREATE TABLE IF NOT EXISTS vod_files (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            game_id     INTEGER UNIQUE NOT NULL,
            file_path   TEXT NOT NULL,
            file_size   INTEGER DEFAULT 0,
            duration_s  INTEGER DEFAULT 0,
            matched_at  INTEGER,
            FOREIGN KEY (game_id) REFERENCES games(game_id)
        );
        """;

    public const string CreateGameEventsTable = """
        CREATE TABLE IF NOT EXISTS game_events (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            game_id     INTEGER NOT NULL,
            event_type  TEXT NOT NULL,
            game_time_s INTEGER NOT NULL,
            details     TEXT DEFAULT '{}',
            FOREIGN KEY (game_id) REFERENCES games(game_id)
        );
        """;

    public const string CreateGameEventsIndex = """
        CREATE INDEX IF NOT EXISTS idx_game_events_game_id
        ON game_events (game_id, game_time_s);
        """;

    public const string CreateVodBookmarksTable = """
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
        """;

    public const string CreateObjectivesTable = """
        CREATE TABLE IF NOT EXISTS objectives (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            title               TEXT NOT NULL,
            skill_area          TEXT DEFAULT '',
            type                TEXT DEFAULT 'primary',
            phase               TEXT DEFAULT 'ingame',
            completion_criteria TEXT DEFAULT '',
            description         TEXT DEFAULT '',
            status              TEXT DEFAULT 'active',
            is_priority         INTEGER DEFAULT 0,
            score               INTEGER DEFAULT 0,
            game_count          INTEGER DEFAULT 0,
            created_at          INTEGER,
            completed_at        INTEGER
        );
        """;

    public const string CreateGameObjectivesTable = """
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
        """;

    public const string CreateConceptTagsTable = """
        CREATE TABLE IF NOT EXISTS concept_tags (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            name     TEXT UNIQUE NOT NULL,
            polarity TEXT DEFAULT 'neutral',
            color    TEXT DEFAULT '#3b82f6'
        );
        """;

    public const string CreateGameConceptTagsTable = """
        CREATE TABLE IF NOT EXISTS game_concept_tags (
            game_id INTEGER NOT NULL,
            tag_id  INTEGER NOT NULL,
            PRIMARY KEY (game_id, tag_id),
            FOREIGN KEY (game_id) REFERENCES games(game_id),
            FOREIGN KEY (tag_id) REFERENCES concept_tags(id)
        );
        """;

    public const string CreateRulesTable = """
        CREATE TABLE IF NOT EXISTS rules (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            name            TEXT NOT NULL,
            description     TEXT DEFAULT '',
            rule_type       TEXT DEFAULT 'custom',
            condition_value TEXT DEFAULT '',
            is_active       INTEGER DEFAULT 1,
            created_at      INTEGER
        );
        """;

    public const string CreateDerivedEventDefinitionsTable = """
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
        """;

    public const string CreateDerivedEventInstancesTable = """
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
        """;

    // Free-form per-objective prompts the user designs as part of objective
    // creation. Rendered in Champ Select (phase='pregame') or post-game review
    // (phase='ingame' or 'postgame'). See docs/OBJECTIVES_CUSTOM_PROMPTS_PLAN.md.
    public const string CreateObjectivePromptsTable = """
        CREATE TABLE IF NOT EXISTS objective_prompts (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            objective_id    INTEGER NOT NULL,
            phase           TEXT NOT NULL DEFAULT 'ingame',
            label           TEXT NOT NULL DEFAULT '',
            sort_order      INTEGER NOT NULL DEFAULT 0,
            created_at      INTEGER,
            FOREIGN KEY (objective_id) REFERENCES objectives(id)
        );
        """;

    public const string CreatePromptAnswersTable = """
        CREATE TABLE IF NOT EXISTS prompt_answers (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            prompt_id   INTEGER NOT NULL,
            game_id     INTEGER NOT NULL,
            answer_text TEXT NOT NULL DEFAULT '',
            updated_at  INTEGER,
            FOREIGN KEY (prompt_id) REFERENCES objective_prompts(id),
            FOREIGN KEY (game_id) REFERENCES games(game_id),
            UNIQUE(prompt_id, game_id)
        );
        """;

    // PreGamePage stages prompt answers here before a game row exists. Keyed
    // on LCU champ-select session id. At post-game, answers get copied into
    // prompt_answers with the real game_id. Drafts are TTL-cleaned.
    public const string CreatePreGameDraftPromptsTable = """
        CREATE TABLE IF NOT EXISTS pre_game_draft_prompts (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            session_key     TEXT NOT NULL,
            prompt_id       INTEGER NOT NULL,
            answer_text     TEXT NOT NULL DEFAULT '',
            updated_at      INTEGER,
            FOREIGN KEY (prompt_id) REFERENCES objective_prompts(id),
            UNIQUE(session_key, prompt_id)
        );
        """;

    // v2.15.0 (later extension): champion gating for objectives. An objective
    // with zero rows here applies to all champions. With rows, it only shows
    // up in pre-game / post-game when the current game's champion matches.
    // Champion names are stored as Riot's display name (e.g. "Kai'Sa", "Wukong")
    // to match games.champion_name verbatim — no id lookup needed.
    public const string CreateObjectiveChampionsTable = """
        CREATE TABLE IF NOT EXISTS objective_champions (
            objective_id    INTEGER NOT NULL,
            champion_name   TEXT NOT NULL,
            PRIMARY KEY (objective_id, champion_name),
            FOREIGN KEY (objective_id) REFERENCES objectives(id)
        );
        """;

    public const string CreateObjectiveChampionsIndex = """
        CREATE INDEX IF NOT EXISTS idx_objective_champions_champion
        ON objective_champions (champion_name);
        """;

    public const string CreateObjectivePromptsIndex = """
        CREATE INDEX IF NOT EXISTS idx_objective_prompts_objective
        ON objective_prompts (objective_id, phase, sort_order);
        """;

    public const string CreatePromptAnswersIndex = """
        CREATE INDEX IF NOT EXISTS idx_prompt_answers_game
        ON prompt_answers (game_id);
        """;

    public const string CreateMatchupNotesTable = """
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
        """;

    public const string CreateSessionsTable = """
        CREATE TABLE IF NOT EXISTS sessions (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            date            TEXT UNIQUE NOT NULL,
            intention       TEXT DEFAULT '',
            debrief_rating  INTEGER DEFAULT 0,
            debrief_note    TEXT DEFAULT '',
            started_at      INTEGER,
            ended_at        INTEGER
        );
        """;

    public const string CreateTiltChecksTable = """
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
            game_id           INTEGER,
            if_then_plan      TEXT DEFAULT '',
            created_at        INTEGER
        );
        """;

    public const string CreateReviewDraftsTable = """
        CREATE TABLE IF NOT EXISTS review_drafts (
            game_id              INTEGER PRIMARY KEY,
            mental_rating        INTEGER DEFAULT 5,
            went_well            TEXT DEFAULT '',
            mistakes             TEXT DEFAULT '',
            focus_next           TEXT DEFAULT '',
            review_notes         TEXT DEFAULT '',
            improvement_note     TEXT DEFAULT '',
            attribution          TEXT DEFAULT '',
            mental_handled       TEXT DEFAULT '',
            spotted_problems     TEXT DEFAULT '',
            outside_control      TEXT DEFAULT '',
            within_control       TEXT DEFAULT '',
            personal_contribution TEXT DEFAULT '',
            enemy_laner          TEXT DEFAULT '',
            matchup_note         TEXT DEFAULT '',
            selected_tag_ids     TEXT DEFAULT '[]',
            objective_assessments TEXT DEFAULT '[]',
            updated_at           INTEGER,
            FOREIGN KEY (game_id) REFERENCES games(game_id)
        );
        """;

    public const string CreateMissedGameDecisionsTable = """
        CREATE TABLE IF NOT EXISTS missed_game_decisions (
            game_id          INTEGER PRIMARY KEY,
            decision         TEXT NOT NULL,
            created_at       INTEGER,
            updated_at       INTEGER
        );
        """;

    // ────────────────────────────────────────────────────────────────────────
    // LEGACY Coach Lab tables (Phase -1 of coach rebuild, 2026-04-18).
    //
    // These 8 tables + 3 migration arrays were the prior "Coach Lab" system
    // (Gemma-only clip ingestion + training pipeline). The code that reads
    // and writes them has been removed; these CREATE statements are kept
    // intentionally so existing user databases are not corrupted by missing
    // table references. No code path in the current app touches any coach_*
    // table. Safe to leave in place. See COACH_CLEANUP_AUDIT.md and
    // COACH_PLAN.md for the replacement architecture.
    // ────────────────────────────────────────────────────────────────────────
    public const string CreateCoachPlayersTable = """
        CREATE TABLE IF NOT EXISTS coach_players (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            display_name    TEXT NOT NULL,
            is_primary      INTEGER DEFAULT 1,
            created_at      INTEGER,
            updated_at      INTEGER
        );
        """;

    public const string CreateCoachObjectiveBlocksTable = """
        CREATE TABLE IF NOT EXISTS coach_objective_blocks (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            player_id       INTEGER NOT NULL,
            objective_id    INTEGER,
            objective_title TEXT DEFAULT '',
            objective_key   TEXT DEFAULT '',
            status          TEXT DEFAULT 'active',
            mode            TEXT DEFAULT 'gemma',
            started_at      INTEGER,
            updated_at      INTEGER,
            completed_at    INTEGER,
            notes           TEXT DEFAULT '',
            FOREIGN KEY (player_id) REFERENCES coach_players(id),
            FOREIGN KEY (objective_id) REFERENCES objectives(id)
        );
        """;

    public const string CreateCoachMomentsTable = """
        CREATE TABLE IF NOT EXISTS coach_moments (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            player_id       INTEGER NOT NULL,
            game_id         INTEGER NOT NULL,
            bookmark_id     INTEGER,
            objective_block_id INTEGER,
            source_type     TEXT NOT NULL,
            patch_version   TEXT DEFAULT 'unknown',
            champion        TEXT DEFAULT '',
            role            TEXT DEFAULT '',
            game_time_s     INTEGER NOT NULL,
            clip_start_s    INTEGER,
            clip_end_s      INTEGER,
            clip_path       TEXT DEFAULT '',
            storyboard_path TEXT DEFAULT '',
            hud_strip_path  TEXT DEFAULT '',
            minimap_strip_path TEXT DEFAULT '',
            manifest_path   TEXT DEFAULT '',
            note_text       TEXT DEFAULT '',
            context_text    TEXT DEFAULT '',
            dataset_version TEXT DEFAULT 'bootstrap-v1',
            model_version   TEXT DEFAULT '',
            created_at      INTEGER,
            reviewed_at     INTEGER,
            FOREIGN KEY (player_id) REFERENCES coach_players(id),
            FOREIGN KEY (game_id) REFERENCES games(game_id),
            FOREIGN KEY (bookmark_id) REFERENCES vod_bookmarks(id),
            FOREIGN KEY (objective_block_id) REFERENCES coach_objective_blocks(id),
            UNIQUE(bookmark_id),
            UNIQUE(game_id, source_type, game_time_s, clip_start_s, clip_end_s)
        );
        """;

    public const string CreateCoachLabelsTable = """
        CREATE TABLE IF NOT EXISTS coach_labels (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            moment_id       INTEGER NOT NULL,
            player_id       INTEGER NOT NULL,
            label_quality   TEXT NOT NULL,
            primary_reason  TEXT DEFAULT '',
            objective_key   TEXT DEFAULT '',
            explanation     TEXT DEFAULT '',
            confidence      REAL DEFAULT 0,
            source          TEXT DEFAULT 'manual',
            created_at      INTEGER,
            updated_at      INTEGER,
            FOREIGN KEY (moment_id) REFERENCES coach_moments(id),
            FOREIGN KEY (player_id) REFERENCES coach_players(id),
            UNIQUE(moment_id)
        );
        """;

    public const string CreateCoachInferencesTable = """
        CREATE TABLE IF NOT EXISTS coach_inferences (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            moment_id       INTEGER NOT NULL,
            player_id       INTEGER NOT NULL,
            model_version   TEXT DEFAULT '',
            inference_mode  TEXT DEFAULT 'gemma',
            moment_quality  TEXT DEFAULT 'neutral',
            primary_reason  TEXT DEFAULT '',
            objective_key   TEXT DEFAULT '',
            confidence      REAL DEFAULT 0,
            rationale       TEXT DEFAULT '',
            raw_payload     TEXT DEFAULT '{}',
            created_at      INTEGER,
            updated_at      INTEGER,
            FOREIGN KEY (moment_id) REFERENCES coach_moments(id),
            FOREIGN KEY (player_id) REFERENCES coach_players(id),
            UNIQUE(moment_id)
        );
        """;

    public const string CreateCoachRecommendationsTable = """
        CREATE TABLE IF NOT EXISTS coach_recommendations (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            objective_block_id INTEGER NOT NULL,
            player_id       INTEGER NOT NULL,
            recommendation_type TEXT DEFAULT 'keep',
            state           TEXT DEFAULT 'draft',
            objective_key   TEXT DEFAULT '',
            title           TEXT DEFAULT '',
            summary         TEXT DEFAULT '',
            confidence      REAL DEFAULT 0,
            evidence_game_count INTEGER DEFAULT 0,
            candidate_snapshot TEXT DEFAULT '[]',
            applied_objective_id INTEGER,
            applied_objective_title TEXT DEFAULT '',
            rejection_reason TEXT DEFAULT '',
            evaluation_window_games INTEGER DEFAULT 0,
            outcome_summary TEXT DEFAULT '',
            feedback_updated_at INTEGER,
            raw_payload     TEXT DEFAULT '{}',
            created_at      INTEGER,
            updated_at      INTEGER,
            FOREIGN KEY (objective_block_id) REFERENCES coach_objective_blocks(id),
            FOREIGN KEY (player_id) REFERENCES coach_players(id)
        );
        """;

    public const string CreateCoachModelsTable = """
        CREATE TABLE IF NOT EXISTS coach_models (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            model_version   TEXT NOT NULL UNIQUE,
            model_kind      TEXT NOT NULL,
            display_name    TEXT DEFAULT '',
            provider        TEXT DEFAULT '',
            is_active       INTEGER DEFAULT 0,
            metadata_json   TEXT DEFAULT '{}',
            created_at      INTEGER
        );
        """;

    public const string CreateCoachDatasetVersionsTable = """
        CREATE TABLE IF NOT EXISTS coach_dataset_versions (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            dataset_version TEXT NOT NULL UNIQUE,
            status          TEXT DEFAULT 'active',
            gold_count      INTEGER DEFAULT 0,
            silver_count    INTEGER DEFAULT 0,
            bronze_count    INTEGER DEFAULT 0,
            reviewed_games  INTEGER DEFAULT 0,
            created_at      INTEGER,
            updated_at      INTEGER
        );
        """;

    // ── Migration statements ─────────────────────────────────────────

    /// <summary>Tilt-check extensions: link to game + implementation-intention plan (Gollwitzer &amp; Sheeran 2006).</summary>
    public static readonly string[] MigrateTiltChecksGameAndPlan =
    [
        "ALTER TABLE tilt_checks ADD COLUMN game_id INTEGER",
        "ALTER TABLE tilt_checks ADD COLUMN if_then_plan TEXT DEFAULT ''",
    ];

    public static readonly string[] MigrateBookmarksClipColumns =
    [
        "ALTER TABLE vod_bookmarks ADD COLUMN clip_start_s INTEGER",
        "ALTER TABLE vod_bookmarks ADD COLUMN clip_end_s INTEGER",
        "ALTER TABLE vod_bookmarks ADD COLUMN clip_path TEXT DEFAULT ''",
    ];

    public static readonly string[] MigrateSessionLogMental =
    [
        "ALTER TABLE session_log ADD COLUMN pregame_intention TEXT DEFAULT ''",
        "ALTER TABLE session_log ADD COLUMN mental_handled TEXT DEFAULT ''",
    ];

    public static readonly string[] MigrateGamesEnemyLaner =
    [
        "ALTER TABLE games ADD COLUMN enemy_laner TEXT DEFAULT ''",
    ];

    public static readonly string[] MigrateGamesSpottedProblems =
    [
        "ALTER TABLE games ADD COLUMN spotted_problems TEXT DEFAULT ''",
    ];

    /// <summary>Cognitive reappraisal fields (Gross 1998/2002; Buhle et al. 2014 meta-analysis).</summary>
    public static readonly string[] MigrateGamesReappraisal =
    [
        "ALTER TABLE games ADD COLUMN outside_control TEXT DEFAULT ''",
        "ALTER TABLE games ADD COLUMN within_control TEXT DEFAULT ''",
    ];

    /// <summary>Attribution tracking (Weiner 1985; Dweck 2006).</summary>
    public static readonly string[] MigrateGamesAttribution =
    [
        "ALTER TABLE games ADD COLUMN attribution TEXT DEFAULT ''",
    ];

    /// <summary>Self-efficacy anchoring (Bandura 1977/1997).</summary>
    public static readonly string[] MigrateGamesSelfEfficacy =
    [
        "ALTER TABLE games ADD COLUMN personal_contribution TEXT DEFAULT ''",
    ];

    /// <summary>Soft-delete: hidden games are excluded from all queries.</summary>
    public static readonly string[] MigrateGamesHidden =
    [
        "ALTER TABLE games ADD COLUMN is_hidden INTEGER NOT NULL DEFAULT 0",
    ];

    /// <summary>Pre-game mood / affect labeling (Lieberman et al. 2007).</summary>
    public static readonly string[] MigrateSessionLogMood =
    [
        "ALTER TABLE session_log ADD COLUMN pre_game_mood INTEGER DEFAULT 0",
    ];

    public static readonly string[] MigrateCoachLabelsAttachment =
    [
        "ALTER TABLE coach_labels ADD COLUMN attached_objective_id INTEGER",
        "ALTER TABLE coach_labels ADD COLUMN attached_objective_title TEXT DEFAULT ''",
    ];

    public static readonly string[] MigrateCoachInferencesAttachment =
    [
        "ALTER TABLE coach_inferences ADD COLUMN attached_objective_id INTEGER",
        "ALTER TABLE coach_inferences ADD COLUMN attached_objective_title TEXT DEFAULT ''",
    ];

    public static readonly string[] MigrateCoachRecommendationsFeedback =
    [
        "ALTER TABLE coach_recommendations ADD COLUMN candidate_snapshot TEXT DEFAULT '[]'",
        "ALTER TABLE coach_recommendations ADD COLUMN applied_objective_id INTEGER",
        "ALTER TABLE coach_recommendations ADD COLUMN applied_objective_title TEXT DEFAULT ''",
        "ALTER TABLE coach_recommendations ADD COLUMN rejection_reason TEXT DEFAULT ''",
        "ALTER TABLE coach_recommendations ADD COLUMN evaluation_window_games INTEGER DEFAULT 0",
        "ALTER TABLE coach_recommendations ADD COLUMN outcome_summary TEXT DEFAULT ''",
        "ALTER TABLE coach_recommendations ADD COLUMN feedback_updated_at INTEGER",
    ];

    public static readonly string[] MigrateObjectivesPriority =
    [
        "ALTER TABLE objectives ADD COLUMN is_priority INTEGER DEFAULT 0",
        "ALTER TABLE objectives ADD COLUMN phase TEXT DEFAULT 'ingame'",
    ];

    /// <summary>
    /// v2.15.0: objectives can practice any subset of {pre, in, post} game
    /// phases instead of being locked to one. Backfilled from the legacy
    /// <c>phase</c> column by DatabaseInitializer.BackfillObjectivePracticePhasesAsync.
    /// </summary>
    public static readonly string[] MigrateObjectivesPracticePhases =
    [
        "ALTER TABLE objectives ADD COLUMN practice_pregame INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE objectives ADD COLUMN practice_ingame INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE objectives ADD COLUMN practice_postgame INTEGER NOT NULL DEFAULT 0",
    ];

    public static readonly string[] MigrateBookmarksObjective =
    [
        "ALTER TABLE vod_bookmarks ADD COLUMN objective_id INTEGER",
        "ALTER TABLE vod_bookmarks ADD COLUMN quality TEXT DEFAULT ''",
    ];

    /// <summary>
    /// v2.15.7: tag a bookmark/clip to a specific custom prompt (not just its
    /// parent objective). When set, post-game review routes the clip's note
    /// into that prompt's answer field. objective_id is also kept populated
    /// for backwards-compat queries.
    /// </summary>
    public static readonly string[] MigrateBookmarksPromptId =
    [
        "ALTER TABLE vod_bookmarks ADD COLUMN prompt_id INTEGER",
    ];

    // ── Aggregated arrays for initialisation ─────────────────────────

    /// <summary>
    /// All CREATE TABLE and CREATE INDEX statements in the order they must be executed.
    /// Matches the execution order in the Python ConnectionManager._init_db().
    /// </summary>
    public static readonly string[] AllCreateStatements =
    [
        CreateGamesTable,
        CreateSessionLogTable,
        CreateReviewDraftsTable,
        CreatePersistentNotesTable,
        CreateVodFilesTable,
        CreateVodBookmarksTable,
        CreateGameEventsTable,
        CreateGameEventsIndex,
        CreateObjectivesTable,
        CreateGameObjectivesTable,
        CreateConceptTagsTable,
        CreateGameConceptTagsTable,
        CreateRulesTable,
        CreateDerivedEventDefinitionsTable,
        CreateDerivedEventInstancesTable,
        CreateObjectivePromptsTable,
        CreatePromptAnswersTable,
        CreatePreGameDraftPromptsTable,
        CreateObjectiveChampionsTable,
        CreateObjectiveChampionsIndex,
        // v2.15.0 NOTE: CreateObjectivePromptsIndex + CreatePromptAnswersIndex
        // reference columns (phase, etc.) that only exist after
        // NormalizeObjectivePromptsTableAsync has run. They're applied
        // separately from DatabaseInitializer after normalize completes.
        CreateMatchupNotesTable,
        CreateSessionsTable,
        CreateTiltChecksTable,
        CreateMissedGameDecisionsTable,
        CreateCoachPlayersTable,
        CreateCoachObjectiveBlocksTable,
        CreateCoachMomentsTable,
        CreateCoachLabelsTable,
        CreateCoachInferencesTable,
        CreateCoachRecommendationsTable,
        CreateCoachModelsTable,
        CreateCoachDatasetVersionsTable,
    ];

    /// <summary>
    /// All ALTER TABLE migration statements flattened into a single array.
    /// Each is executed inside a try/catch to tolerate "duplicate column" errors.
    /// </summary>
    public static readonly string[] AllMigrations =
    [
        .. MigrateGamesEnemyLaner,
        .. MigrateGamesSpottedProblems,
        .. MigrateGamesReappraisal,
        .. MigrateGamesAttribution,
        .. MigrateGamesSelfEfficacy,
        .. MigrateSessionLogMood,
        .. MigrateBookmarksClipColumns,
        .. MigrateSessionLogMental,
        .. MigrateObjectivesPriority,
        .. MigrateObjectivesPracticePhases,
        .. MigrateCoachLabelsAttachment,
        .. MigrateCoachInferencesAttachment,
        .. MigrateCoachRecommendationsFeedback,
        .. MigrateGamesHidden,
        .. MigrateBookmarksObjective,
        .. MigrateBookmarksPromptId,
        .. MigrateTiltChecksGameAndPlan,
    ];

    // ── Default seed data ────────────────────────────────────────────

    /// <summary>Default concept tags: (Name, Polarity, Color).</summary>
    public static readonly (string Name, string Polarity, string Color)[] DefaultConceptTags =
    [
        ("Dominated lane",    "positive", "#22c55e"),
        ("Won teamfight",     "positive", "#22c55e"),
        ("Good roam",         "positive", "#22c55e"),
        ("Objective control", "positive", "#22c55e"),
        ("Survived early",    "positive", "#22c55e"),
        ("Caught out",        "negative", "#ef4444"),
        ("Bad trade",         "negative", "#ef4444"),
        ("Poor micro",        "negative", "#ef4444"),
        ("Poor macro",        "negative", "#ef4444"),
        ("Tilted",            "negative", "#ef4444"),
        ("Overextended",      "negative", "#ef4444"),
        ("Even game",         "neutral",  "#3b82f6"),
        ("Team diff",         "neutral",  "#3b82f6"),
        ("Passive game",      "neutral",  "#3b82f6"),
    ];

    /// <summary>Default derived event definitions: (Name, SourceTypes, MinCount, WindowSeconds, Color).</summary>
    public static readonly (string Name, string SourceTypes, int MinCount, int WindowSeconds, string Color)[] DefaultDerivedEvents =
    [
        ("Teamfight",         "[\"KILL\",\"DEATH\"]",                    3, 15, "#ff6b6b"),
        ("Skirmish",          "[\"KILL\",\"DEATH\"]",                    2, 10, "#ffa07a"),
        ("Objective Contest",  "[\"DRAGON\",\"BARON\",\"KILL\",\"DEATH\"]",  2, 30, "#c89b3c"),
        ("Death Streak",      "[\"DEATH\"]",                          2, 60, "#ea5455"),
        ("Tower Dive",        "[\"TURRET\",\"KILL\",\"DEATH\"]",          2, 10, "#f97316"),
    ];
}

#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A timestamped in-game event (kill, death, objective, etc.).
/// </summary>
public class GameEvent
{
    public int Id { get; set; }
    public long GameId { get; set; }
    public string EventType { get; set; } = "";
    public int GameTimeS { get; set; }
    public string Details { get; set; } = "{}";

    /// <summary>Event type constant strings.</summary>
    public static class EventTypes
    {
        public const string Kill = "KILL";
        public const string Death = "DEATH";
        public const string Assist = "ASSIST";
        public const string Dragon = "DRAGON";
        public const string Baron = "BARON";
        public const string Herald = "HERALD";
        public const string Turret = "TURRET";
        public const string Inhibitor = "INHIBITOR";
        public const string FirstBlood = "FIRST_BLOOD";
        public const string MultiKill = "MULTI_KILL";
        public const string LevelUp = "LEVEL_UP";
        // v2.17.7: summoner spell casts derived from /liveclientdata/activeplayer
        // cooldown deltas. Flash gets its own type because it's the spell players
        // most often want to track for review. The generic SummonerSpell type
        // covers everything else (Ignite, Heal, Teleport, Smite, Exhaust, Cleanse,
        // Barrier, Ghost — Details.spell carries the specific name).
        public const string Flash = "FLASH";
        public const string SummonerSpell = "SUMMONER_SPELL";
        // v3.0.18: recall (back), DERIVED — Riot's API emits no recall event, so we
        // infer it from a shop purchase (gold drops while alive ⇒ you're at fountain
        // ⇒ you just recalled). The event is anchored ~8s before the purchase (the
        // recall channel time). Details carries gold_spent + that it's detected.
        public const string Recall = "RECALL";
    }

    /// <summary>Visual styling for each event type (color, symbol, label).</summary>
    public static class EventStyles
    {
        public static readonly IReadOnlyDictionary<string, (string Color, string Symbol, string Label)> Styles =
            new Dictionary<string, (string, string, string)>
            {
                { EventTypes.Kill,       ("#28c76f", "\u25B2", "Kill") },
                { EventTypes.Death,      ("#ea5455", "\u25BC", "Death") },
                { EventTypes.Assist,     ("#0099ff", "\u25CF", "Assist") },
                { EventTypes.Dragon,     ("#c89b3c", "\u25C6", "Dragon") },
                { EventTypes.Baron,      ("#8b5cf6", "\u25C6", "Baron") },
                { EventTypes.Herald,     ("#06b6d4", "\u25C6", "Herald") },
                { EventTypes.Turret,     ("#f97316", "\u25A0", "Turret") },
                { EventTypes.Inhibitor,  ("#ec4899", "\u25A0", "Inhibitor") },
                { EventTypes.FirstBlood, ("#ef4444", "\u2605", "First Blood") },
                { EventTypes.MultiKill,  ("#fbbf24", "\u2605", "Multi Kill") },
                { EventTypes.LevelUp,    ("#6366f1", "\u2191", "Level Up") },
                { EventTypes.Flash,         ("#06b6d4", "\u26a1", "Flash") },
                { EventTypes.SummonerSpell, ("#0099ff", "\u26a1", "Summoner Spell") },
            };
    }

    /// <summary>
    /// The vocabulary of "trackable tokens" a user can tie to an objective. This is a
    /// SUPERSET of the raw EventTypes: each summoner spell is its own token (so e.g.
    /// only Smite usage can be tracked), and TEAMFIGHT is a synthetic token (a derived
    /// cluster of combat events, not a stored row). The raw game_events storage model
    /// is unchanged \u2014 these finer tokens are derived at read time (per-spell from
    /// Details.spell, teamfights from a combat-cluster pass). LEVEL_UP is intentionally
    /// excluded (it is never emitted). One source of truth shared by the objective-edit
    /// picker option list and the VOD timeline matcher.
    /// </summary>
    public static class TrackableTokens
    {
        public const string TeamfightToken = "TEAMFIGHT";
        public const string SpellPrefix = "SPELL_";

        /// <summary>The nine real League summoner spells, as SPELL_* tokens. The bare
        /// name (after the prefix, title-cased) matches Details.spell from the live API.</summary>
        public static readonly string[] SpellNames =
            ["Flash", "Ignite", "Teleport", "Smite", "Exhaust", "Heal", "Barrier", "Cleanse", "Ghost"];

        /// <summary>Token \u2192 (Group, Label, ColorHex). Order within a group is display order.</summary>
        public static readonly IReadOnlyList<(string Token, string Group, string Label, string Color)> Catalog =
        [
            // Combat
            (EventTypes.Kill,       "Combat",     "Kill",        "#28c76f"),
            (EventTypes.Death,      "Combat",     "Death",       "#ea5455"),
            (EventTypes.Assist,     "Combat",     "Assist",      "#0099ff"),
            (EventTypes.MultiKill,  "Combat",     "Multikill",   "#fbbf24"),
            (EventTypes.FirstBlood, "Combat",     "First Blood", "#ef4444"),
            // Objectives
            (EventTypes.Dragon,     "Objectives", "Dragon",      "#c89b3c"),
            (EventTypes.Baron,      "Objectives", "Baron",       "#8b5cf6"),
            (EventTypes.Herald,     "Objectives", "Herald",      "#06b6d4"),
            (EventTypes.Turret,     "Objectives", "Turret",      "#f97316"),
            (EventTypes.Inhibitor,  "Objectives", "Inhibitor",   "#ec4899"),
            // Summoners (one token per spell)
            ("SPELL_FLASH",    "Summoners", "Flash",    "#7fd4ff"),
            ("SPELL_IGNITE",   "Summoners", "Ignite",   "#ff7043"),
            ("SPELL_TELEPORT", "Summoners", "Teleport", "#5c8dff"),
            ("SPELL_SMITE",    "Summoners", "Smite",    "#9ccc65"),
            ("SPELL_EXHAUST",  "Summoners", "Exhaust",  "#ffca5f"),
            ("SPELL_HEAL",     "Summoners", "Heal",     "#7fe3c0"),
            ("SPELL_BARRIER",  "Summoners", "Barrier",  "#ffd54f"),
            ("SPELL_CLEANSE",  "Summoners", "Cleanse",  "#80deea"),
            ("SPELL_GHOST",    "Summoners", "Ghost",    "#b39ddb"),
            // Macro (derived from shop purchases)
            (EventTypes.Recall, "Macro",    "Recall",    "#a9c8ff"),
            // Fights (synthetic derived token)
            (TeamfightToken,   "Fights",    "Teamfight", "#f3a3a8"),
        ];

        private static readonly HashSet<string> _valid =
            new(Catalog.Select(c => c.Token), StringComparer.OrdinalIgnoreCase);

        /// <summary>True if the token is a recognized trackable token (case-insensitive).</summary>
        public static bool IsValid(string? token) => token is not null && _valid.Contains(token);

        /// <summary>Color for a token, or "" if unknown.</summary>
        public static string ColorOf(string token)
        {
            foreach (var c in Catalog)
                if (string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase)) return c.Color;
            return "";
        }
    }
}

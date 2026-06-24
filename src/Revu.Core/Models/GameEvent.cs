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
        // v3.1.8: trade (you took damage and lived), DERIVED — the Live Client API
        // exposes only YOUR championStats HP per poll tick (never the enemy's), so a
        // "trade" is inferred as a meaningful HP drop while alive (not a death). One
        // event type carries the severity in Details.kind ("short" | "extended"):
        // a single-tick drop reads short; damage sustained across consecutive ticks
        // reads extended. Heuristic, hence Details.detected = true. Replaces the
        // un-ingestible summoner-spell tokens in the objective picker.
        public const string Trade = "TRADE";
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
                { EventTypes.Recall,        ("#a9c8ff", "\u21ba", "Recall") },
                { EventTypes.Trade,         ("#ffb86b", "\u2694", "Trade") },
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

        /// <summary>The trade family. A stored TRADE row matches the generic
        /// <see cref="TradeToken"/> (any trade) AND the kind-specific token derived
        /// from Details.kind: "short" → <see cref="ShortTradeToken"/>, "extended" →
        /// <see cref="ExtendedTradeToken"/>. So an objective can track all trades or
        /// just one severity. (Mirrors how a summoner cast matched both a generic and
        /// a per-spell token before the Summoners group was retired.)</summary>
        public const string TradeToken = EventTypes.Trade;        // "TRADE"
        public const string ShortTradeToken = "SHORT_TRADE";
        public const string ExtendedTradeToken = "EXTENDED_TRADE";

        /// <summary>A DEATH where the enemy JUNGLER was on the kill (killer or assister)
        /// during laning phase — i.e. a jungle gank. It's an ATTRIBUTE of a DEATH row
        /// (Details.jungle_gank = true), not a separate event, so the death audit and
        /// timeline don't double-count. A jungle-ganked death matches BOTH this token and
        /// the plain DEATH token (mirrors how a TRADE matches its kind token + generic).</summary>
        public const string JungleGankToken = "JUNGLE_GANK";

        /// <summary>The nine real League summoner spells, as SPELL_* tokens. The bare
        /// name (after the prefix, title-cased) matches Details.spell from the live API.
        /// Retained for back-compat: the Summoners group was removed from the picker
        /// (summoner-cast timing can't be ingested — see flash-investigation), but rows
        /// stored before then still derive these tokens at read time.</summary>
        public static readonly string[] SpellNames =
            ["Flash", "Ignite", "Teleport", "Smite", "Exhaust", "Heal", "Barrier", "Cleanse", "Ghost"];

        /// <summary>Token \u2192 (Group, Label, ColorHex). Order within a group is display order.</summary>
        public static readonly IReadOnlyList<(string Token, string Group, string Label, string Color)> Catalog =
        [
            // Combat
            (EventTypes.Kill,       "Combat",     "Kill",          "#28c76f"),
            (EventTypes.Death,      "Combat",     "Death",         "#ea5455"),
            (JungleGankToken,       "Combat",     "Died to Gank",  "#d6455e"),
            (EventTypes.Assist,     "Combat",     "Assist",        "#0099ff"),
            (EventTypes.MultiKill,  "Combat",     "Multikill",   "#fbbf24"),
            (EventTypes.FirstBlood, "Combat",     "First Blood", "#ef4444"),
            // Objectives
            (EventTypes.Dragon,     "Objectives", "Dragon",      "#c89b3c"),
            (EventTypes.Baron,      "Objectives", "Baron",       "#8b5cf6"),
            (EventTypes.Herald,     "Objectives", "Herald",      "#06b6d4"),
            (EventTypes.Turret,     "Objectives", "Turret",      "#f97316"),
            (EventTypes.Inhibitor,  "Objectives", "Inhibitor",   "#ec4899"),
            // Lane (derived) — recall from shop purchases / fountain restore, and
            // trades from your own HP dropping while alive. The Summoners group used
            // to live here (one token per spell) but summoner-cast timing can't be
            // ingested from any ToS-safe source, so it was retired (v3.1.8).
            (EventTypes.Recall,  "Lane", "Recall",         "#a9c8ff"),
            (TradeToken,         "Lane", "Trade",          "#ffb86b"),
            (ShortTradeToken,    "Lane", "Short Trade",    "#ffd9a3"),
            (ExtendedTradeToken, "Lane", "Extended Trade", "#ff9248"),
            // Fights (synthetic derived token)
            (TeamfightToken,   "Fights",    "Teamfight", "#f3a3a8"),
        ];

        /// <summary>Legacy per-spell tokens (SPELL_FLASH, …) that are NO LONGER in the
        /// picker Catalog but stay VALID for persistence so objectives tied to summoner
        /// spells before v3.1.8 keep working — their stored ties round-trip through a
        /// re-save and the resolver still matches them. They're just not offered for new
        /// selection. Built from <see cref="SpellNames"/> so the two can't drift.</summary>
        private static readonly string[] LegacySpellTokens =
            SpellNames.Select(n => SpellPrefix + n.ToUpperInvariant()).ToArray();

        // The validity set is the picker Catalog PLUS the legacy per-spell tokens, so
        // removing the Summoners group from the picker doesn't silently drop the ties of
        // objectives that already track a summoner spell.
        private static readonly HashSet<string> _valid =
            new(Catalog.Select(c => c.Token).Concat(LegacySpellTokens), StringComparer.OrdinalIgnoreCase);

        /// <summary>True if the token is a recognized trackable token (case-insensitive),
        /// including the retired-from-the-picker but still-valid legacy spell tokens.</summary>
        public static bool IsValid(string? token) => token is not null && _valid.Contains(token);

        /// <summary>Color for a token, or "" if unknown. Falls back to the summoner cyan
        /// for legacy SPELL_* tokens (dropped from the Catalog but still rendered).</summary>
        public static string ColorOf(string token)
        {
            foreach (var c in Catalog)
                if (string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase)) return c.Color;
            if (token is not null && token.StartsWith(SpellPrefix, StringComparison.OrdinalIgnoreCase))
                return "#7fd4ff"; // summoner cyan — matches the retired Summoners group
            return "";
        }
    }
}

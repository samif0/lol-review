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
            };
    }
}

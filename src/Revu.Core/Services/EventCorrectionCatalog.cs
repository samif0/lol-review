#nullable enable

using System.Text.Json.Nodes;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

/// <summary>
/// The event types a reviewer can correct and, per type, the attributes the fix panel may edit.
/// One source of truth for the VOD panel (served in the snapshot as eventTypeCatalog) and for
/// <see cref="EventCorrectionsRepository"/> validation, so JS never hardcodes a type or a value.
/// </summary>
public static class EventCorrectionCatalog
{
    public sealed record AttrOption(string Value, string Label);
    public sealed record AttrDef(string Key, string Label, string Input /* "bool" | "choice" */, IReadOnlyList<AttrOption> Options);
    public sealed record TypeDef(string Type, string Label, string Kind /* "point" | "span" */, string Color, IReadOnlyList<AttrDef> Attrs);

    public const string InputBool = "bool";
    public const string InputChoice = "choice";
    public const string KindPoint = "point";
    public const string KindSpan = "span";

    public const string AttrNotEditableMessage = "That attribute is not editable for this event type.";
    public const string AttrValueMessage = "Choose one of the listed values.";

    /// <summary>Display order. Labels/colors come from GameEvent.TrackableTokens.Catalog where the
    /// type token exists; FLASH, SUMMONER_SPELL, LEVEL_UP are not offered.</summary>
    public static readonly IReadOnlyList<TypeDef> Types =
    [
        new("KILL", "Kill", KindPoint, "#28c76f", []),
        new("DEATH", "Death", KindPoint, "#ea5455",
        [
            new("jungle_gank", "Died to gank", InputBool, []),
            new("fog_death", "Died in fog", InputBool, []),
        ]),
        new("ASSIST", "Assist", KindPoint, "#0099ff", []),
        new("MULTI_KILL", "Multikill", KindPoint, "#fbbf24", []),
        new("FIRST_BLOOD", "First Blood", KindPoint, "#ef4444", []),
        new("DRAGON", "Dragon", KindPoint, "#c89b3c", []),
        new("BARON", "Baron", KindPoint, "#8b5cf6", []),
        new("HERALD", "Herald", KindPoint, "#06b6d4", []),
        new("TURRET", "Turret", KindPoint, "#f97316", []),
        new("INHIBITOR", "Inhibitor", KindPoint, "#ec4899", []),
        new("RECALL", "Recall", KindPoint, "#a9c8ff", []),
        new("TRADE", "Trade", KindSpan, "#ffb86b",
        [
            new("kind", "Trade length", InputChoice, [new("short", "Short trade"), new("extended", "Extended trade")]),
        ]),
        new("ALL_IN", "All-in", KindSpan, "#f87171", []),
        new("UNCERTAIN_COMBAT", "Uncertain combat", KindSpan, "#9ca3af", []),
        new("JUNGLE_PROXIMITY", "Jungler near", KindPoint, "#b07cd8",
        [
            new("who", "Whose jungler", InputChoice, [new("enemy", "Enemy jungler"), new("ally", "Ally jungler")]),
        ]),
        new("TEAMFIGHT", "Teamfight", KindSpan, "#f3a3a8",
        [
            new("self", "Were you in it", InputChoice, [new("in", "You were in it"), new("away", "Without you")]),
            new("verdict", "Numbers when you committed", InputChoice,
                [new("up", "Numbers up"), new("even", "Even numbers"), new("down", "Outnumbered")]),
        ]),
    ];

    /// <summary>Case-insensitive lookup; null for an unknown or empty type.</summary>
    public static TypeDef? Find(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        foreach (var def in Types)
            if (string.Equals(def.Type, type.Trim(), StringComparison.OrdinalIgnoreCase)) return def;
        return null;
    }

    public static bool IsKnownType(string? type) => Find(type) is not null;
    public static bool IsSpan(string? type) => Find(type)?.Kind == KindSpan;
    public static string LabelOf(string? type) => Find(type)?.Label ?? (type ?? "");

    /// <summary>null when valid, else the user-facing message
    /// "That attribute is not editable for this event type." or "Choose one of the listed values."
    /// Bool attrs accept JSON true/false only; choice attrs accept one of the option values
    /// (case-insensitive).</summary>
    public static string? ValidateAttrs(string type, IReadOnlyDictionary<string, JsonNode?> attrs)
    {
        var def = Find(type);
        foreach (var pair in attrs)
        {
            var attr = def?.Attrs.FirstOrDefault(a => string.Equals(a.Key, pair.Key, StringComparison.Ordinal));
            if (attr is null) return AttrNotEditableMessage;
            if (pair.Value is not JsonValue value) return AttrValueMessage;
            if (attr.Input == InputBool)
            {
                if (!value.TryGetValue<bool>(out _)) return AttrValueMessage;
                continue;
            }
            if (!value.TryGetValue<string>(out var s)
                || !attr.Options.Any(o => string.Equals(o.Value, s.Trim(), StringComparison.OrdinalIgnoreCase)))
                return AttrValueMessage;
        }
        return null;
    }
}

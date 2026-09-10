#nullable enable

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>The seven ledger ops. Six are saveable through <see cref="IEventCorrectionsRepository.SaveAsync"/>;
/// <see cref="Revert"/> rows are written only by <see cref="IEventCorrectionsRepository.RevertAsync"/>.</summary>
public static class CorrectionOps
{
    public const string Retype = "retype";
    public const string Retime = "retime";
    public const string Attr = "attr";
    public const string Remove = "remove";
    public const string Add = "add";
    public const string Confirm = "confirm";
    public const string Revert = "revert";
    public static readonly string[] Saveable = [Retype, Retime, Attr, Remove, Add, Confirm];
    public static bool IsSaveable(string? op) => op is not null && Array.IndexOf(Saveable, op) >= 0;
}

/// <summary>Ledger row states. A correction leaves <see cref="Active"/> when a newer fix on the same
/// subject supersedes it, when the user reverts it, when the detector reproduces it (absorbed) or when
/// its subject can no longer be found (orphaned).</summary>
public static class CorrectionStates
{
    public const string Active = "active";
    public const string Superseded = "superseded";
    public const string Reverted = "reverted";
    public const string Absorbed = "absorbed";
    public const string Orphaned = "orphaned";
    /// <summary>States whose correction still applies to the timeline.</summary>
    public static readonly string[] Applicable = [Active, Absorbed, Orphaned];
}

public static class CorrectionShareStates
{
    public const string Held = "held";
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>Which detector produced the subject row, recorded at save time.</summary>
public static class CorrectionDetectors
{
    public const string Live = "live";
    public const string MapState = "map_state";
    public const string Teamfight = "teamfight";
    public const string ReviewedEncounter = "reviewed_encounter";
    public const string Manual = "manual";
}

/// <summary>The cumulative change a correction applies on top of <see cref="EventOriginal"/>.
/// Attrs values are JSON scalars (bool, string, number). Confirmed is set only by op confirm
/// and cleared by any later fix.</summary>
public sealed record EventPatch(
    string? EventType,
    int? GameTimeS,
    int? EndS,
    IReadOnlyDictionary<string, JsonNode?>? Attrs,
    bool Confirmed = false)
{
    public static readonly EventPatch Empty = new(null, null, null, null);

    public bool IsEmpty => EventType is null && GameTimeS is null && EndS is null
        && (Attrs is null || Attrs.Count == 0) && !Confirmed;

    /// <summary>{"event_type"?,"game_time_s"?,"end_s"?,"attrs"?:{},"confirmed"?:true}. Malformed
    /// input parses to <see cref="Empty"/>, never throws.</summary>
    public static EventPatch Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return Empty;
            Dictionary<string, JsonNode?>? attrs = null;
            if (o["attrs"] is JsonObject a && a.Count > 0)
            {
                attrs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                foreach (var pair in a) attrs[pair.Key] = pair.Value?.DeepClone();
            }
            return new EventPatch(
                EventJson.ReadString(o, "event_type"),
                EventJson.ReadInt(o, "game_time_s"),
                EventJson.ReadInt(o, "end_s"),
                attrs,
                EventJson.ReadBool(o, "confirmed") == true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return Empty;
        }
    }

    public string ToJson()
    {
        var o = new JsonObject();
        if (EventType is not null) o["event_type"] = EventType;
        if (GameTimeS is not null) o["game_time_s"] = GameTimeS.Value;
        if (EndS is not null) o["end_s"] = EndS.Value;
        if (Attrs is { Count: > 0 })
        {
            var a = new JsonObject();
            foreach (var key in AttrKeys) a[key] = Attrs[key]?.DeepClone();
            o["attrs"] = a;
        }
        if (Confirmed) o["confirmed"] = true;
        return o.ToJsonString();
    }

    /// <summary>Cumulative merge: scalar fields of <paramref name="delta"/> win when present,
    /// attrs merge key-wise, Confirmed = delta.Confirmed.</summary>
    public EventPatch Overlay(EventPatch delta)
    {
        Dictionary<string, JsonNode?>? attrs = null;
        if ((Attrs is { Count: > 0 }) || (delta.Attrs is { Count: > 0 }))
        {
            attrs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (Attrs is not null)
                foreach (var pair in Attrs) attrs[pair.Key] = pair.Value?.DeepClone();
            if (delta.Attrs is not null)
                foreach (var pair in delta.Attrs) attrs[pair.Key] = pair.Value?.DeepClone();
        }
        return new EventPatch(
            delta.EventType ?? EventType,
            delta.GameTimeS ?? GameTimeS,
            delta.EndS ?? EndS,
            attrs,
            delta.Confirmed);
    }

    public IReadOnlyList<string> AttrKeys => Attrs is null ? [] : Attrs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
}

/// <summary>The detected row as first corrected. Details is the row's details JSON with the
/// correction marker stripped. For op add: ("", 0, "{}").</summary>
public sealed record EventOriginal(string EventType, int GameTimeS, string Details)
{
    public static readonly EventOriginal None = new("", 0, "{}");

    /// <summary>{"event_type":"DEATH","game_time_s":812,"details":{...}} (details is an OBJECT, not a string).
    /// Malformed input parses to <see cref="None"/>, never throws.</summary>
    public static EventOriginal Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return None;
            var type = EventJson.ReadString(o, "event_type") ?? "";
            if (type.Length == 0) return None;
            var details = o["details"] switch
            {
                JsonObject d => d.ToJsonString(),
                JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => s,
                _ => "{}",
            };
            return new EventOriginal(type, EventJson.ReadInt(o, "game_time_s") ?? 0, details);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return None;
        }
    }

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["event_type"] = EventType,
            ["game_time_s"] = GameTimeS,
            ["details"] = EventJson.ParseObject(Details),
        };
        return o.ToJsonString();
    }

    public bool IsNone => EventType.Length == 0;

    public GameEvent ToEvent(long gameId) => new() { GameId = gameId, EventType = EventType, GameTimeS = GameTimeS, Details = Details };
}

/// <summary>One event_corrections row.</summary>
public sealed record EventCorrection(
    long Id,
    string CorrectionId,
    long GameId,
    string SubjectKey,
    string SubjectType,
    int SubjectTimeS,
    string Op,
    EventPatch Patch,
    EventOriginal Original,
    string Reason,
    string Detector,
    int? DetectorVersion,
    string AppVersion,
    long? SupersedesId,
    string RebasedFrom,
    int? DeltaS,
    string State,
    long? AppliedEventId,
    long? AppliedAt,
    string ApplyError,
    string ShareState,
    long? SharedAt,
    long CreatedAt,
    long UpdatedAt)
{
    public bool IsApplicable => Op != CorrectionOps.Revert && Array.IndexOf(CorrectionStates.Applicable, State) >= 0;

    /// <summary>Effective type/time after the cumulative patch.</summary>
    public string EffectiveType => Patch.EventType ?? SubjectType;
    public int EffectiveTimeS => Patch.GameTimeS ?? SubjectTimeS;
}

public sealed record EventCorrectionSubject(string? EventKey, long? EventId, string Type, int TimeS);

public sealed record EventCorrectionRequest(
    long GameId,
    string CorrectionId,
    string Op,
    EventCorrectionSubject? Subject,   // null for op add
    EventPatch Patch,                  // EventPatch.Empty for remove/confirm
    string Reason,                     // <= 280 after trim
    string AppVersion = "",
    string? EncounterNote = null);     // legacy /api/encounter/save note (<= 2000) written to details.note

public sealed record EventCorrectionSaveResult(
    long Id,
    string CorrectionId,
    string Op,
    string State,
    string EventKey,
    long? AppliedEventId,
    bool Idempotent,
    string Message);                   // "" or a user-facing note (death cause reset)

/// <summary>Small tolerant JSON readers shared by the ledger types. Every reader returns null
/// (never throws) when the key is missing or the value has another shape.</summary>
internal static class EventJson
{
    public static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new JsonObject();
        }
    }

    public static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static string? ReadString(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    public static int? ReadInt(JsonObject? o, string key) => AsInt(o?[key]);

    public static bool? ReadBool(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    public static int? AsInt(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
        if (v.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d)) return (int)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static string? AsString(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        return v.ToJsonString();
    }
}

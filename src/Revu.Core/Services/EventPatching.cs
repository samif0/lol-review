#nullable enable

using System.Text.Json.Nodes;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Applies and undoes a cumulative <see cref="EventPatch"/> on a <see cref="GameEvent"/> in
/// memory. Pure: the callers (the ledger repository, the applier) own the SQL. A corrected row
/// carries <c>details.correction = {"id","op","attrs":[...],"confirmed"?}</c>, the marker every
/// game_events writer uses to recognise a row it must not delete or re-detect.
/// </summary>
public static class EventPatching
{
    public const string MarkerKey = "correction";

    public static bool HasMarker(string details) =>
        EventJson.TryParseObject(details)?[MarkerKey] is JsonObject;

    public static (string Id, string Op)? ReadMarker(string details)
    {
        if (EventJson.TryParseObject(details)?[MarkerKey] is not JsonObject m) return null;
        return (EventJson.ReadString(m, "id") ?? "", EventJson.ReadString(m, "op") ?? "");
    }

    /// <summary>Removes <c>details.correction</c>.</summary>
    public static void StripMarker(JsonObject details) => details.Remove(MarkerKey);

    /// <summary>The attribute keys a correction wrote on this row (<c>details.correction.attrs</c>);
    /// empty when there is no marker or it is malformed. An additive stamper (the map-state pass
    /// parses the row's existing details and only ever adds keys) removes these before it decides,
    /// so rule C can read presence as "the detector wrote this" instead of echoing the fix.</summary>
    public static IReadOnlyList<string> CorrectedAttrKeys(JsonObject? details)
    {
        if (details?[MarkerKey] is not JsonObject m || m["attrs"] is not JsonArray attrs) return [];
        var keys = new List<string>(attrs.Count);
        foreach (var node in attrs)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var key) && key.Length > 0) keys.Add(key);
        }
        return keys;
    }

    public static bool IsEncounterType(string type) =>
        type is GameEvent.EventTypes.Trade or GameEvent.EventTypes.AllIn or GameEvent.EventTypes.UncertainCombat;

    /// <summary>Apply a CUMULATIVE patch to <paramref name="row"/> in place (type, time, details,
    /// EventKey = subjectKey). Details keys untouched by the patch are preserved.
    /// Rules:
    ///  - EventType := patch.EventType ?? row.EventType
    ///  - newTime := patch.GameTimeS ?? AnchorOf(row); row.GameTimeS = newTime
    ///  - TEAMFIGHT or encounter type: details.start_s = newTime; end = patch.EndS ?? details.end_s ?? newTime,
    ///    clamped &gt;= newTime; details.end_s = end; details.duration_s = end - newTime
    ///  - attrs overlaid key by key (JsonNode deep clone)
    ///  - encounter type: source="reviewed_encounter", reviewed=true, classification_version=1,
    ///    request_id ??= correctionId, classification = TRADE ? (attrs.kind ?? details.kind ?? "short")
    ///    : ALL_IN ? "all_in" : "uncertain", kind = TRADE ? classification : null,
    ///    note = encounterNote ?? (reason.Length &gt; 0 ? reason : details.note ?? ""),
    ///    and when original is a detected row (not None) and original.EventType differs from the
    ///    effective type or original.GameTimeS differs: original_type, original_time_s,
    ///    original_details (original.Details as a JSON STRING, the legacy shape)
    ///  - effective type NOT an encounter type and details.source == "reviewed_encounter": remove source, reviewed
    ///  - details.correction = {"id":correctionId,"op":op,"attrs":patch.AttrKeys,"confirmed":true when patch.Confirmed}
    /// </summary>
    public static void Apply(GameEvent row, EventPatch patch, EventOriginal original,
        string correctionId, string op, string subjectKey, string reason, string? encounterNote = null)
    {
        var details = EventJson.ParseObject(row.Details);
        var effectiveType = (patch.EventType ?? row.EventType).ToUpperInvariant();
        var newTime = patch.GameTimeS ?? EventIdentity.AnchorOf(row);

        row.EventType = effectiveType;
        row.GameTimeS = newTime;

        var isEncounter = IsEncounterType(effectiveType);
        if (isEncounter || effectiveType == GameEvent.TrackableTokens.TeamfightToken)
        {
            var end = patch.EndS ?? EventJson.ReadInt(details, "end_s") ?? newTime;
            if (end < newTime) end = newTime;
            details["start_s"] = newTime;
            details["end_s"] = end;
            details["duration_s"] = end - newTime;
        }

        if (patch.Attrs is not null)
        {
            foreach (var pair in patch.Attrs)
                details[pair.Key] = pair.Value?.DeepClone();
        }

        if (isEncounter)
        {
            details["source"] = ReviewedEncountersRepository.Source;
            details["reviewed"] = true;
            details["classification_version"] = 1;
            details["request_id"] ??= correctionId;
            string classification;
            if (effectiveType == GameEvent.EventTypes.Trade)
            {
                var kind = (patch.Attrs is not null && patch.Attrs.TryGetValue("kind", out var k) ? EventJson.AsString(k) : null)
                    ?? EventJson.ReadString(details, "kind");
                classification = string.IsNullOrWhiteSpace(kind) ? "short" : kind.Trim().ToLowerInvariant();
                details["kind"] = classification;
            }
            else
            {
                classification = effectiveType == GameEvent.EventTypes.AllIn ? "all_in" : "uncertain";
                details["kind"] = null;
            }
            details["classification"] = classification;
            details["note"] = encounterNote ?? (reason.Length > 0 ? reason : EventJson.ReadString(details, "note") ?? "");
            if (!original.IsNone
                && (!string.Equals(original.EventType, effectiveType, StringComparison.OrdinalIgnoreCase)
                    || original.GameTimeS != newTime))
            {
                details["original_type"] = original.EventType;
                details["original_time_s"] = original.GameTimeS;
                details["original_details"] = original.Details;
            }
        }
        else if (EventJson.ReadString(details, "source") == ReviewedEncountersRepository.Source)
        {
            details.Remove("source");
            details.Remove("reviewed");
        }

        var marker = new JsonObject
        {
            ["id"] = correctionId,
            ["op"] = op,
            ["attrs"] = new JsonArray(patch.AttrKeys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
        };
        if (patch.Confirmed) marker["confirmed"] = true;
        details[MarkerKey] = marker;

        row.Details = details.ToJsonString();
        row.EventKey = subjectKey;
    }

    /// <summary>Restore type/time/details from the original snapshot; marker stripped; EventKey kept.</summary>
    public static void Restore(GameEvent row, EventOriginal original)
    {
        var details = EventJson.ParseObject(original.Details);
        StripMarker(details);
        row.EventType = original.EventType;
        row.GameTimeS = original.GameTimeS;
        row.Details = details.ToJsonString();
    }

    /// <summary>Snapshot a row as an original: details with the marker stripped.</summary>
    public static EventOriginal Snapshot(GameEvent row)
    {
        var details = EventJson.ParseObject(row.Details);
        StripMarker(details);
        return new EventOriginal(row.EventType, row.GameTimeS, details.ToJsonString());
    }

    /// <summary>(detector, detector_v):
    ///  source == reviewed_encounter -> ("reviewed_encounter", classification_version ?? 1)
    ///  TEAMFIGHT -> ("teamfight", details.v)
    ///  JUNGLE_PROXIMITY -> ("map_state", details.det_v)
    ///  DEATH with details.map_state true, or attrs containing fog_death -> ("map_state", details.det_v)
    ///  op add (or an original that is None) -> ("manual", null)
    ///  else -> ("live", details.det_v)</summary>
    public static (string Detector, int? Version) DetectorOf(string op, EventOriginal original, EventPatch patch)
    {
        if (op == CorrectionOps.Add || original.IsNone) return (CorrectionDetectors.Manual, null);
        var details = EventJson.TryParseObject(original.Details);
        if (EventJson.ReadString(details, "source") == ReviewedEncountersRepository.Source)
            return (CorrectionDetectors.ReviewedEncounter, EventJson.ReadInt(details, "classification_version") ?? 1);
        var type = original.EventType.ToUpperInvariant();
        if (type == GameEvent.TrackableTokens.TeamfightToken)
            return (CorrectionDetectors.Teamfight, EventJson.ReadInt(details, "v"));
        if (type == GameEvent.EventTypes.JungleProximity)
            return (CorrectionDetectors.MapState, EventJson.ReadInt(details, "det_v"));
        if (type == GameEvent.EventTypes.Death
            && (EventJson.ReadBool(details, "map_state") == true
                || (patch.Attrs is not null && patch.Attrs.ContainsKey("fog_death"))))
            return (CorrectionDetectors.MapState, EventJson.ReadInt(details, "det_v"));
        return (CorrectionDetectors.Live, EventJson.ReadInt(details, "det_v"));
    }
}

#nullable enable

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Turns the POST /api/event/correct wire body into the Core <see cref="EventCorrectionRequest"/>.
/// Only shape work happens here (trim, case, JsonElement to JsonNode); every rule and every
/// user-facing message stays in <see cref="EventCorrectionsRepository"/>. The one exception is
/// a non-object attrs payload, which cannot be expressed as a request at all and is refused
/// with the catalog's own value message.
/// </summary>
internal static class CorrectionRequestMapper
{
    public static EventCorrectionRequest From(SaveCorrectionBody body, string appVersion)
    {
        EventCorrectionSubject? subject = body.Subject is { } s
            ? new EventCorrectionSubject(
                EventKey: string.IsNullOrWhiteSpace(s.EventKey) ? null : s.EventKey.Trim(),
                EventId: s.EventId,
                Type: (s.Type ?? "").Trim().ToUpperInvariant(),
                TimeS: s.TimeS ?? -1)
            : null;

        var patch = EventPatch.Empty;
        if (body.Patch is { } p)
        {
            patch = new EventPatch(
                string.IsNullOrWhiteSpace(p.EventType) ? null : p.EventType.Trim().ToUpperInvariant(),
                p.GameTimeS,
                p.EndS,
                ReadAttrs(p.Attrs));
        }

        return new EventCorrectionRequest(
            GameId: body.GameId,
            CorrectionId: (body.CorrectionId ?? "").Trim(),
            Op: (body.Op ?? "").Trim().ToLowerInvariant(),
            Subject: subject,
            Patch: patch,
            Reason: (body.Reason ?? "").Trim(),
            AppVersion: appVersion ?? "");
    }

    private static IReadOnlyDictionary<string, JsonNode?>? ReadAttrs(JsonElement? attrs)
    {
        if (attrs is not { } a || a.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(a.GetRawText()) as JsonObject; }
        catch (JsonException) { o = null; }
        if (o is null) throw new ArgumentException(EventCorrectionCatalog.AttrValueMessage);
        var dict = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var pair in o) dict[pair.Key] = pair.Value?.DeepClone();
        return dict;
    }
}

/// <summary>
/// Ledger rows and the correction catalog as the VOD snapshot / GET /api/corrections
/// serve them: the type catalog the fix panel builds its selects from (so JS never
/// hardcodes a type or a value) and one <see cref="VodCorrectionDto"/> per ledger row
/// with a human summary ("Death moved 13:32 to 13:35").
/// </summary>
public static class VodCorrectionMapper
{
    /// <summary>The correctable types, in display order, from <see cref="EventCorrectionCatalog.Types"/>.</summary>
    public static readonly IReadOnlyList<VodEventTypeDto> Catalog = EventCorrectionCatalog.Types
        .Select(t => new VodEventTypeDto(
            Type: t.Type,
            Label: t.Label,
            Kind: t.Kind,
            ColorHex: t.Color,
            Attrs: t.Attrs
                .Select(a => new VodEventAttrDto(
                    Key: a.Key,
                    Label: a.Label,
                    Input: a.Input,
                    Options: a.Options.Select(o => new VodEventAttrOptionDto(o.Value, o.Label)).ToList()))
                .ToList()))
        .ToList();

    public static VodCorrectionDto Map(EventCorrection c) => new(
        Id: c.Id,
        CorrectionId: c.CorrectionId,
        Op: c.Op,
        State: c.State,
        StateLabel: c.State == CorrectionStates.Active ? "applied" : c.State,
        SubjectKey: c.SubjectKey,
        SubjectType: c.SubjectType,
        SubjectTimeSeconds: c.SubjectTimeS,
        EventType: c.EffectiveType,
        GameTimeSeconds: c.EffectiveTimeS,
        TimeLabel: FormatClock(c.EffectiveTimeS),
        Summary: Summary(c),
        Reason: c.Reason,
        AppliedEventId: c.AppliedEventId,
        ApplyError: c.ApplyError,
        CreatedAt: c.CreatedAt,
        CanRevert: c.IsApplicable);

    /// <summary>retype "{Orig} at {t0} is now {New}"; retime "{Label} moved {t0} to {t1}";
    /// attr "{Label} at {t}: {key} = {value}[, ...]"; remove "{Label} at {t} removed";
    /// add "{Label} added at {t}"; confirm "{Label} at {t} confirmed".</summary>
    public static string Summary(EventCorrection c)
    {
        var label = EventCorrectionCatalog.LabelOf(c.EffectiveType);
        var original = EventCorrectionCatalog.LabelOf(c.SubjectType);
        var t0 = FormatClock(c.SubjectTimeS);
        var t = FormatClock(c.EffectiveTimeS);
        return c.Op switch
        {
            CorrectionOps.Retype => $"{original} at {t0} is now {label}",
            CorrectionOps.Retime => $"{label} moved {t0} to {t}",
            CorrectionOps.Attr => $"{label} at {t}: {AttrList(c.Patch)}",
            CorrectionOps.Remove => $"{original} at {t0} removed",
            CorrectionOps.Add => $"{label} added at {t}",
            CorrectionOps.Confirm => $"{label} at {t} confirmed",
            CorrectionOps.Revert => $"{original} at {t0} reverted",
            _ => $"{label} at {t}",
        };
    }

    private static string AttrList(EventPatch patch)
    {
        if (patch.Attrs is null || patch.Attrs.Count == 0) return "no change";
        return string.Join(", ", patch.AttrKeys.Select(k => $"{k} = {ValueText(patch.Attrs[k])}"));
    }

    private static string ValueText(JsonNode? value)
    {
        if (value is not JsonValue v) return value is null ? "null" : value.ToJsonString();
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        return v.ToJsonString();
    }

    // Mirrors VodSnapshotBuilder.FormatClock ("12:41").
    private static string FormatClock(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }
}

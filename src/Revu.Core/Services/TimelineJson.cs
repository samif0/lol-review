#nullable enable

using System.Text.Json;

namespace Revu.Core.Services;

/// <summary>Tolerant readers for the Match-V5 JSON the post-game analyzers walk:
/// a missing or mistyped property reads as "" / 0 / empty, never throws.</summary>
internal static class TimelineJson
{
    internal static IReadOnlyList<int> ReadIntArray(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<int>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number) result.Add(item.GetInt32());
        return result;
    }

    internal static string Str(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    internal static int Int(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    internal static long Long(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : 0;
}

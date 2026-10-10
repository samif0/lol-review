#nullable enable

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revu.Core.Services;

/// <summary>
/// The C2 transcript document: stored in <c>clip_narrations.transcript_json</c>, sent as the
/// <c>PUT /clips/:id/transcript</c> body and served by <c>GET /clip-transcript/:id</c>.
/// Times are seconds on the clip timeline (t=0 is the first frame of the narrated clip).
/// </summary>
public sealed record TranscriptDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments)
{
    public const int CurrentVersion = 1;
    public const int MaxSegments = 3000;
    public const int MaxSegmentChars = 500;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Build a C2 document: clamp to [0, clipDurationS], drop empty or non-positive
    /// segments, collapse whitespace, cap text at 500 chars, sort by start, round to 3
    /// decimals, keep at most 3000 segments.
    /// </summary>
    public static TranscriptDocument Normalize(IEnumerable<TranscriptSegment> segments, string? language, double clipDurationS)
    {
        var limit = double.IsFinite(clipDurationS) && clipDurationS > 0 ? clipDurationS : double.MaxValue;
        var kept = new List<TranscriptSegment>();
        foreach (var seg in segments ?? Array.Empty<TranscriptSegment>())
        {
            if (seg is null || !double.IsFinite(seg.Start) || !double.IsFinite(seg.End)) continue;
            var text = CollapseWhitespace(seg.Text);
            if (text.Length == 0) continue;
            if (text.Length > MaxSegmentChars) text = text[..MaxSegmentChars].TrimEnd();

            var start = Math.Round(Math.Clamp(seg.Start, 0, limit), 3, MidpointRounding.AwayFromZero);
            var end = Math.Round(Math.Clamp(seg.End, 0, limit), 3, MidpointRounding.AwayFromZero);
            if (end <= start) continue;
            kept.Add(new TranscriptSegment(start, end, text));
        }

        var ordered = kept.OrderBy(s => s.Start).ThenBy(s => s.End).Take(MaxSegments).ToList();
        return new TranscriptDocument(CurrentVersion, NormalizeLanguage(language), ordered);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Parse a stored document; null when the text is empty or not a valid C2 document.</summary>
    public static TranscriptDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<TranscriptDocument>(json, Options);
            if (doc is null || doc.Segments is null) return null;
            if (doc.Segments.Any(s => s is null || s.Text is null)) return null;
            return doc with { Language = doc.Language ?? "" };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>"" or a 2..8 char lowercase tag (C2 language rule).</summary>
    public static string NormalizeLanguage(string? language)
    {
        var lang = (language ?? "").Trim().ToLowerInvariant();
        return lang.Length is >= 2 and <= 8 ? lang : "";
    }

    private static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>One C2 transcript line (seconds on the clip timeline).</summary>
public sealed record TranscriptSegment(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("text")] string Text);

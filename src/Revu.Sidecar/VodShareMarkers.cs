#nullable enable

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Sidecar;

/// <summary>
/// Builds the markers document v1 a full-game share carries (contract 6.3). Privacy rules:
/// markers are the VOD page's own eligible timeline events, as type and label only (never
/// summaries, encounter notes, objective titles, rosters or corrections); game meta has no
/// Riot IDs or account names; bookmarks and clip notes go in <c>notes</c> only when the player
/// ticked "Include my notes and bookmarks", and never become markers.
/// </summary>
internal static class VodShareMarkers
{
    private const double MaxGameSeconds = 10800;
    private const int MaxMarkers = 1000;
    private const int MaxNotes = 200;
    private const int LabelMaxChars = 40;
    private const int KindMaxChars = 32;
    private const int NoteMaxChars = 500;
    private const int NoteTrimChars = 280;

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal sealed record Result(string Json, int Markers, int Notes, int MarkersDropped, int NotesDropped);

    public static Result Build(VodDto vod, GameStats? game, bool includeNotes)
    {
        var markers = BuildMarkers(vod);
        var total = markers.Count;
        while (markers.Count > MaxMarkers || Size(MarkersNode(markers)) > GameConstants.VodShareMarkersBudgetBytes)
        {
            // Thin evenly: drop every second marker (index 1, 3, 5, ...) and measure again.
            var kept = markers.Where((_, i) => i % 2 == 0).ToList();
            if (kept.Count == markers.Count) break;
            markers = kept;
        }

        List<NoteItem>? notes = includeNotes ? BuildNotes(vod) : null;
        var notesTotal = notes?.Count ?? 0;
        if (notes is not null)
        {
            if (notes.Count > MaxNotes) notes = notes.Take(MaxNotes).ToList();
            if (Size(NotesNode(notes)) > GameConstants.VodShareNotesBudgetBytes)
            {
                notes = notes.Select(n => n with { Text = Cut(n.Text, NoteTrimChars) }).ToList();
                while (notes.Count > 0 && Size(NotesNode(notes)) > GameConstants.VodShareNotesBudgetBytes)
                    notes.RemoveAt(notes.Count - 1);
            }
        }

        var doc = new JsonObject
        {
            ["version"] = 1,
            ["gameTimeAtVideoStart"] = VideoStart(vod.GameTimeAtVideoStart),
            ["game"] = GameNode(vod, game),
            ["markers"] = MarkersNode(markers),
            ["notes"] = notes is null ? null : NotesNode(notes),
        };
        return new Result(doc.ToJsonString(Options), markers.Count, notes?.Count ?? 0,
            total - markers.Count, notesTotal - (notes?.Count ?? 0));
    }

    private sealed record MarkerItem(double T, string Kind, string Label, string? Tone);

    private sealed record NoteItem(double T, double? End, string Kind, string Text);

    private static List<MarkerItem> BuildMarkers(VodDto vod)
    {
        var list = new List<MarkerItem>();
        foreach (var e in vod.GameEvents ?? Array.Empty<VodEventDto>())
        {
            if (e is null || e.Removed) continue;
            if (e.GameTimeSeconds < 0 || e.GameTimeSeconds > MaxGameSeconds) continue;
            var kind = Kind(e.EventType);
            if (kind.Length == 0) continue;
            var label = Cut(Clean(e.Label), LabelMaxChars);
            if (label.Length == 0) label = kind;
            var tone = e.Kind is "win" or "loss" or "gold" or "neutral" ? e.Kind : null;
            list.Add(new MarkerItem(e.GameTimeSeconds, kind, label, tone));
        }
        return list.OrderBy(m => m.T).ToList();
    }

    private static List<NoteItem> BuildNotes(VodDto vod)
    {
        var list = new List<NoteItem>();
        foreach (var b in vod.Bookmarks ?? Array.Empty<VodBookmarkDto>())
        {
            if (b is null || b.GameTimeSeconds < 0 || b.GameTimeSeconds > MaxGameSeconds) continue;
            double? end = b.HasClip && b.ClipEndSeconds is { } e && e >= b.GameTimeSeconds && e <= MaxGameSeconds ? e : null;
            list.Add(new NoteItem(b.GameTimeSeconds, end, b.HasClip ? "clip" : "bookmark",
                Cut(Clean(b.Note), NoteMaxChars)));
        }
        return list.OrderBy(n => n.T).ToList();
    }

    private static JsonObject GameNode(VodDto vod, GameStats? game)
    {
        var node = new JsonObject();
        void Put(string key, string? value)
        {
            var v = Cut(Clean(value), LabelMaxChars);
            if (v.Length > 0) node[key] = v;
        }

        Put("champion", Pick(game?.ChampionName, vod.ChampionName));
        Put("opponentChampion", Pick(game?.EnemyLaner, vod.EnemyChampion));
        var role = Pick(game?.Position, game?.Role).Trim();
        if (role.Length is > 0 and <= 16 && role.All(char.IsAsciiLetter)) node["role"] = role.ToUpperInvariant();
        if (game is not null)
        {
            node["result"] = game.IsRemake ? "remake" : game.Win ? "win" : "loss";
            if (game.GameDuration > 0 && game.GameDuration <= MaxGameSeconds) node["durationS"] = game.GameDuration;
            if (game.Timestamp > 0)
            {
                node["playedAt"] = DateTimeOffset.FromUnixTimeSeconds(game.Timestamp).ToLocalTime()
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        else if (vod.GameDurationSeconds > 0 && vod.GameDurationSeconds <= MaxGameSeconds)
        {
            node["durationS"] = vod.GameDurationSeconds;
        }
        return node;
    }

    private static JsonArray MarkersNode(IEnumerable<MarkerItem> markers)
    {
        var array = new JsonArray();
        foreach (var m in markers)
        {
            var o = new JsonObject { ["t"] = m.T, ["kind"] = m.Kind, ["label"] = m.Label };
            if (m.Tone is not null) o["tone"] = m.Tone;
            array.Add(o);
        }
        return array;
    }

    private static JsonArray NotesNode(IEnumerable<NoteItem> notes)
    {
        var array = new JsonArray();
        foreach (var n in notes)
        {
            var o = new JsonObject { ["t"] = n.T };
            if (n.End is { } end) o["end"] = end;
            o["kind"] = n.Kind;
            o["text"] = n.Text;
            array.Add(o);
        }
        return array;
    }

    private static int Size(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString(Options));

    /// <summary>The source's game time at video 0, clamped to [-600, 7200] and rounded to 0.01 (0 when not finite).</summary>
    internal static double VideoStart(double value) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, -600, 7200), 2, MidpointRounding.AwayFromZero) : 0;

    /// <summary>EventType uppercased, only [A-Z0-9_], at most 32 characters.</summary>
    internal static string Kind(string? eventType)
    {
        var sb = new StringBuilder();
        foreach (var c in (eventType ?? "").ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') sb.Append(c);
            if (sb.Length == KindMaxChars) break;
        }
        return sb.ToString();
    }

    private static string Pick(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first! : second ?? "";

    /// <summary>Line breaks and tabs become spaces, other control characters go, then trimmed.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\r' or '\n' or '\t') sb.Append(' ');
            else if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>At most <paramref name="max"/> UTF-16 units, never splitting a surrogate pair.</summary>
    private static string Cut(string value, int max)
    {
        if (value.Length <= max) return value;
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut].TrimEnd();
    }
}

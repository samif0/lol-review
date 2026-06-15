#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/tiltcheck.
//
// These reproduce the WinUI Tilt Check page's display shape WITHOUT any
// XAML/WinUI types: there are NO SolidColorBrush properties — every color is a
// plain *Hex string the Tauri frontend resolves itself. Property names are
// PascalCase here; the serializer is configured with
// JsonNamingPolicy.CamelCase in Program.cs, so the wire shape is camelCase and
// matches desktop/ui/sample-tiltcheck.json verbatim.
//
// READ-ONLY: this endpoint is a pure read slice — recent history, aggregate
// stats, and the latest if-then plan. The tilt-reset RITUAL itself is a WRITE
// the frontend performs via invoke('run_reset', {...}) → POST /api/reset; the
// snapshot below has no mutation surface.
//
// Null vs empty:
//   - `recent` is always present (possibly empty), never null.
//   - `latestPlan` is null when no non-empty plan exists within the window.
//   - `intensityAfter` / `gameId` on a row are null when the column is NULL.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level tilt-check snapshot returned by GET /api/tiltcheck.</summary>
public sealed record TiltCheckDto(
    string GeneratedAt,
    // Recent tilt-reset rituals, newest first (up to 20). Always an array.
    IReadOnlyList<TiltCheckEntryDto> Recent,
    bool HasRecent,
    // Aggregate stats over rituals that recorded an "after" intensity.
    TiltCheckStatsDto Stats,
    // Latest non-empty if-then plan (≤14d), or null. Display-only — never scored.
    string? LatestPlan,
    bool HasLatestPlan);

/// <summary>
/// One recorded tilt-reset ritual. Mirrors a row of the tilt_checks table; the
/// before/after intensities drive the reduction display and the reframe pair is
/// the cognitive-restructuring record.
/// </summary>
public sealed record TiltCheckEntryDto(
    long Id,
    string Emotion,
    int IntensityBefore,
    // null when the ritual was logged without an "after" rating.
    int? IntensityAfter,
    // before - after when both present, else null. Positive = calmer.
    int? IntensityReduction,
    string ReframeThought,
    string ReframeResponse,
    string ThoughtType,
    string CueWord,
    string FocusIntention,
    // Linked game id, or null when the reset wasn't tied to a game.
    long? GameId,
    string IfThenPlan,
    bool HasPlan,
    // Unix seconds (UTC), as stored.
    long CreatedAt,
    // Local "MMM dd, HH:mm" for the row, "" when timestamp missing.
    string CreatedAtText,
    // Emotion chip color. Reset emotions map to the loss-red HUD accent.
    string EmotionColorHex);

/// <summary>
/// Aggregate tilt-check statistics. Mirrors Revu.Core's <c>TiltCheckStats</c>:
/// averages are over rituals that recorded an "after" intensity only.
/// </summary>
public sealed record TiltCheckStatsDto(
    int Total,
    double AvgBefore,
    double AvgAfter,
    double AvgReduction,
    // "−1.8 avg" style headline for the reduction stat, "" when no rated rituals.
    string AvgReductionText,
    IReadOnlyList<EmotionCountDto> TopEmotions);

/// <summary>One emotion frequency entry (emotion → count), most frequent first.</summary>
public sealed record EmotionCountDto(
    string Emotion,
    int Count,
    // Emotion chip color (loss-red HUD accent), mirrors entry rows.
    string EmotionColorHex);

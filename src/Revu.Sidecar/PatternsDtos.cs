#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/patterns.
//
// Read-only mirror of the WinUI Pattern Review surface
// (PatternReviewViewModel + PatternMomentItem), WITHOUT any XAML/WinUI types:
// there are NO SolidColorBrush properties — every color is a plain *Hex string
// the Tauri frontend resolves itself. Property names are PascalCase here; the
// serializer is configured with JsonNamingPolicy.CamelCase in Program.cs, so the
// wire shape is camelCase and matches desktop/ui/sample-patterns.json verbatim.
//
// One pattern card carries its full ordered moment playlist (oldest-first) so the
// frontend can render the cross-game pattern cards + drill into each moment
// without a second round-trip. "Mark reviewed" is a WRITE and is DEFERRED — we
// surface the reviewed state (isReviewed) and a carry-forward note placeholder
// for display only; no write endpoint is added here.
//
// Null vs empty:
//   - moment.startTimeSeconds / endTimeSeconds are nullable (null = no timed
//     window on that evidence row), matching the Core PatternMoment record.
//   - Collections (patterns, moments) are always present (possibly empty), never
//     null.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level patterns snapshot returned by GET /api/patterns.</summary>
public sealed record PatternsSnapshotDto(
    string GeneratedAt,
    // Distinct patterns the user has already marked reviewed (dashboard stat).
    int ReviewedPatternCount,
    // True when at least one pattern is still pending review.
    bool HasPending,
    int PendingCount,
    // Empty-state copy shown when there are no pattern cards at all.
    string EmptyText,
    IReadOnlyList<PatternCardDto> Patterns);

/// <summary>
/// One cross-game pattern card with its ordered moment playlist. Mirrors
/// PatternReviewViewModel's header fields (title / severity / subtitle) plus the
/// ObjectivePatternCard identity used to track review state.
/// </summary>
public sealed record PatternCardDto(
    // Stable identity (ObjectivePatternCard.PatternKey) — kind, or kind:objNN.
    string PatternKey,
    string Kind,
    string Title,
    string Detail,
    long? GameId,
    long? ObjectiveId,
    // "high" | "medium" | "low" (raw) — frontend styles the badge from this.
    string Severity,
    // Uppercased severity badge label (e.g. "HIGH"), mirrors SeverityLabel.
    string SeverityLabel,
    // "high" -> negative red, else gold. No SolidColorBrush. Mirrors SeverityHex.
    string SeverityHex,
    // True once the user has marked this pattern reviewed (write deferred — read
    // state only). Mirrors PatternReviewViewModel.IsReviewed.
    bool IsReviewed,
    int MomentCount,
    int GameCount,
    // "N moments across M games" / "No moments are still pending …".
    string Subtitle,
    // Carry-forward note placeholder — display only; the write that sets it is
    // DEFERRED. Always "" for now so the field is stable in the contract.
    string CarryForwardNote,
    IReadOnlyList<PatternMomentDto> Moments);

/// <summary>
/// One moment composing a pattern — an evidence item joined to its game's
/// champion/result and matched VOD path. Mirrors PatternMomentItem's display
/// fields (labels + accent hexes) with NO brushes.
/// </summary>
public sealed record PatternMomentDto(
    long EvidenceId,
    long GameId,
    // 1-based position within the pattern playlist (oldest-first).
    int Ordinal,
    string ChampionName,
    // "Game" when champion is blank, else the champion name. Mirrors ChampionLabel.
    string ChampionLabel,
    bool Win,
    // "WIN" | "LOSS". Mirrors ResultLabel.
    string ResultLabel,
    // Positive green / negative red. Mirrors ResultHex. No brush.
    string ResultHex,
    long GameTimestamp,
    int? StartTimeSeconds,
    int? EndTimeSeconds,
    // "m:ss" of the moment start. Mirrors TimeLabel.
    string TimeLabel,
    // "Champ · WIN · m:ss" header over the video. Mirrors VideoHeaderText.
    string VideoHeaderText,
    string Title,
    string Note,
    bool HasNote,
    // "good" | "bad" | "neutral" (raw polarity).
    string Polarity,
    // "GOOD" | "BAD" | "NEUTRAL". Mirrors PolarityLabel.
    string PolarityLabel,
    // Positive/negative/neutral hex for the polarity accent. Mirrors AccentHex.
    string AccentHex,
    string SourceKind,
    string VodPath,
    // True when this moment has a matched VOD on disk to play. Mirrors HasVod.
    bool HasVod);

#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/objective/games?id=N (ObjectiveGamesPage).
//
// A drill-down detail page for ONE objective: all games linked to it + an
// Evidence Ledger of clips/notes tagged to it. READ-ONLY — every jump (Watch
// VOD / Review) is plain file-route navigation handled by the frontend; this
// endpoint never writes.
//
// As with the other builders there are NO SolidColorBrush properties — colors
// arrive as plain *Hex strings the frontend applies to style props. Property
// names are PascalCase; the serializer is JsonNamingPolicy.CamelCase, so the
// wire shape is camelCase (see desktop/ui/sample-objective-games.json).
//
// Null vs empty: collections are always present (possibly empty), never null.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level snapshot returned by GET /api/objective/games?id=N.</summary>
public sealed record ObjectiveGamesDto(
    string GeneratedAt,
    long ObjectiveId,
    // Header: title + "Active • In-Game" / "Completed • Pre-Game" composite.
    string ObjectiveTitle,
    string ObjectiveStatus,
    // "{practiced} practiced / {total} total" — only meaningful when HasGames.
    string CounterText,
    int TotalCount,
    int PracticedCount,
    bool HasGames,
    bool HasEvidence,
    // HasGames || HasEvidence — drives the content-vs-empty switch.
    bool HasActivity,
    // "3 evidence item(s)  /  1 good  /  1 bad  /  1 neutral" or
    // "No linked evidence yet.".
    string EvidenceSummary,
    IReadOnlyList<ObjectiveGameRowDto> Games,
    IReadOnlyList<ObjectiveEvidenceRowDto> Evidence);

/// <summary>One game linked to the objective. Mirrors ObjectiveGameRow.</summary>
public sealed record ObjectiveGameRowDto(
    long GameId,
    string ChampionName,
    bool Win,
    // "W" | "L".
    string ResultText,
    // Win → win green, loss → loss red.
    string ResultColorHex,
    // "MMM d, yyyy" (local) or "".
    string DateText,
    // "12/3/8" (K/D/A as F0).
    string KdaText,
    bool Practiced,
    // "Practiced" | "Skipped".
    string PracticedText,
    // Practiced → positive green, skipped → neutral muted.
    string PracticedColorHex,
    string PracticedDimColorHex,
    // Per-game execution note (only shown when HasExecutionNote).
    string ExecutionNote,
    bool HasExecutionNote);

/// <summary>One evidence-ledger row. Mirrors ObjectiveEvidenceRow.</summary>
public sealed record ObjectiveEvidenceRowDto(
    long GameId,
    string Title,
    // champion / date / time joined by "  /  " skipping blanks.
    string MetaText,
    // Suppressed (empty) when blank OR equal to Title (case-insensitive).
    string DisplayNote,
    bool HasDisplayNote,
    // "good" | "bad" | "neutral" (normalized).
    string Polarity,
    // "Good example" | "Bad example" | "Neutral".
    string PolarityLabel,
    // Accent for the polarity chip + row border.
    string PolarityColorHex);

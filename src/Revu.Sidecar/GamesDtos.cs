#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/games (the History page).
//
// Same conventions as Dtos.cs: PascalCase here, camelCase on the wire (the
// serializer in Program.cs uses JsonNamingPolicy.CamelCase). NO SolidColorBrush
// — every color is a plain *Hex string the Tauri frontend resolves itself.
//
// V1 paging: the endpoint returns the FIRST page (30 rows) plus totalCount and
// hasMore. Server-side paging beyond page 0 is a TODO (see GamesSnapshotBuilder).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level games-workspace snapshot returned by GET /api/games.</summary>
public sealed record GamesDto(
    string GeneratedAt,
    // Active view echoed back: "queue" | "today" | "history" | "vod" (mirrors
    // the ?view= request; unknown/missing → "queue").
    string View,
    // Per-view heading, e.g. "Review Queue" / "Today" / "History" / "VOD Review".
    string Heading,
    // Zero-based History page (offset = Page * PageSize). 0 for the other views.
    int Page,
    int PageSize,
    // Rows actually returned in this page (<= PageSize).
    int ReturnedCount,
    // History: total ranked/normal games across all pages. Queue/Today/VOD:
    // the returned count (those views are single-shot, no further pages).
    int TotalCount,
    // Display string, e.g. "42 games" / "1 game".
    string CountText,
    // True only for History when more pages remain; always false for the
    // single-shot Queue/Today/VOD views (mirrors ApplyViewCopy).
    bool HasMore,
    bool IsEmpty,
    string EmptyMessage,
    IReadOnlyList<GamesRowDto> Items);

/// <summary>
/// One history game row. Mirrors the dashboard's GameDisplayItemDto (same
/// flattened, display-ready fields + precomputed format strings — KdaText,
/// StatsLine WITHOUT vision, MetaLine) and adds the History-specific
/// enrichment state (VOD availability + objective tagging) and a stable
/// <see cref="Action"/> token. Emits only *Hex color strings, never brushes.
/// </summary>
public sealed record GamesRowDto(
    long GameId,
    string ChampionName,
    string EnemyChampion,
    string GameRole,
    bool Win,
    string WinLossText,
    int Kills,
    int Deaths,
    int Assists,
    double KdaRatio,
    string KdaText,
    string KdaRatioText,
    int CsTotal,
    double CsPerMin,
    int VisionScore,
    int TotalDamageToChampions,
    string Duration,
    string DatePlayed,
    string GameMode,
    string WinLossColorHex,
    string BorderColorHex,
    bool HasReview,
    string DamageText,
    // Stats line WITHOUT vision: "CS NNN (X.X/m)  ·  NNk dmg".
    string StatsLine,
    // Meta line: "GAMEMODE  ·  DATE  ·  DURATION".
    string MetaLine,
    // ── History enrichment (mirror GamesViewModel.EnrichRowsAsync) ──────────
    // True when a VOD file is linked AND exists on disk.
    bool HasVod,
    bool ObjectivePracticed,
    bool HasObjectiveEvidence,
    // "Evidence tagged" / "VOD evidence pending" / "Objective practiced" / "No objective tag".
    string ObjectiveStateText,
    // "Reviewed" / "Unreviewed".
    string ReviewStateText,
    // "VOD linked" / "No VOD".
    string VodStateText,
    // Inline button label: "Watch VOD" / "Open" / "Review" (VOD wins, v2.17.8).
    string PrimaryAction,
    // Row-body click token the Tauri shell maps to navigation. "open_review".
    string Action);

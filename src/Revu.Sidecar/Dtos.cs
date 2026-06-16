#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/dashboard.
//
// These reproduce the WinUI DashboardViewModel's display shape WITHOUT any
// XAML/WinUI types: there are NO SolidColorBrush properties — every color is a
// plain *Hex string the Tauri frontend resolves itself. Property names are
// PascalCase here; the serializer is configured with
// JsonNamingPolicy.CamelCase in Program.cs, so the wire shape is camelCase and
// matches desktop/sample-dashboard.json verbatim.
//
// Null vs empty:
//   - intent.sessionIntention / debriefRating are nullable (null = no Start
//     Block ritual today), matching the sample contract.
//   - Collections are always present (possibly empty), never null.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level dashboard snapshot returned by GET /api/dashboard.</summary>
public sealed record DashboardDto(
    string GeneratedAt,
    string Today,
    string Greeting,
    DashboardStatsDto Stats,
    NextStepDto NextStep,
    IntentDto Intent,
    DeathMixDto DeathMix,
    VodPendingDto VodPending,
    UnreviewedDto Unreviewed,
    IReadOnlyList<ActiveObjectiveDto> ActiveObjectives,
    PatternsDto Patterns);

/// <summary>Hero stat strip — today's numbers framed against the 30-day baseline.</summary>
public sealed record DashboardStatsDto(
    int TotalGames,
    int Wins,
    int Losses,
    // Em-dash ("—") when there are zero games today, else "NN%".
    string WinratePercent,
    string WinRateSub,
    double AvgMental,
    string AvgMentalSub,
    int AdherenceStreak,
    string AdherenceSub,
    int ReviewedPatternCount,
    string PatternsReviewedSub);

/// <summary>
/// Empty-state "next step" card copy. Derived from the dashboard stage. The
/// frontend already renders this; <see cref="Action"/> is a stable token the
/// Tauri shell maps to navigation.
/// </summary>
public sealed record NextStepDto(
    string Kicker,
    string Title,
    string Detail,
    string CtaLabel,
    string Action);

/// <summary>
/// The active block's Start Block intent + End Block debrief (null when unset).
/// BlockDate is the date the active/open block belongs to — usually today, but it
/// can be an earlier day when a block was started and never ended (it carries over
/// so End Block can still close it). CarriedOver marks that prior-day case so the UI
/// can hint "wrap your last block". End Block must target BlockDate, not today.
/// </summary>
public sealed record IntentDto(
    string? SessionIntention,
    int? DebriefRating,
    string? BlockDate = null,
    bool CarriedOver = false);

/// <summary>
/// 14-day classified death mix. <see cref="Text"/> is the ready-to-show
/// sentence; <see cref="Label"/>/<see cref="Pct"/>/<see cref="Sample"/> are the
/// structured parts so the frontend can style the cause + percentage spans
/// without string-parsing. All empty/zero when fewer than 5 deaths are tagged.
/// </summary>
public sealed record DeathMixDto(
    string Text,
    string Label,
    string Pct,
    int Sample);

/// <summary>First reviewed game that has a VOD but no objective-tagged evidence yet.</summary>
public sealed record VodPendingDto(
    bool Show,
    long GameId,
    string Text);

/// <summary>The unreviewed-games queue (capped at 8 items).</summary>
public sealed record UnreviewedDto(
    int Count,
    string CountText,
    bool AllReviewed,
    IReadOnlyList<GameDisplayItemDto> Items);

/// <summary>One active objective card (mastery ladder or mini focus drill).</summary>
public sealed record ActiveObjectiveDto(
    long Id,
    string Title,
    string PhaseLabel,
    bool IsMini,
    int TargetGameCount,
    string LevelName,
    int Score,
    int GameCount,
    double Progress,
    string LevelColorHex,
    string LevelDimColorHex,
    string InfoText,
    bool IsPriority,
    string ProgressLabel,
    string MetaText);

/// <summary>The cross-game pattern nag (at most 4 still-pending patterns).</summary>
public sealed record PatternsDto(
    bool Has,
    IReadOnlyList<ObjectivePatternItemDto> Items);

/// <summary>One pattern card. <see cref="AccentHex"/> replaces the WinUI AccentBrush.</summary>
public sealed record ObjectivePatternItemDto(
    string Kind,
    string Title,
    string Detail,
    long? GameId,
    long? ObjectiveId,
    string Severity,
    // Severity "high" -> negative red, else gold. No SolidColorBrush.
    string AccentHex);

/// <summary>
/// Flattened, display-ready game row. Mirrors the WinUI GameDisplayItem's
/// fields and precomputed format strings (KdaText, StatsLine, MetaLine, etc.)
/// but emits only *Hex color strings — never brushes.
/// </summary>
public sealed record GameDisplayItemDto(
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
    string StatsLine,
    string MetaLine);

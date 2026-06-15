#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/rules (the Rules page).
//
// Same conventions as DashboardDtos.cs / GamesDtos.cs: PascalCase here,
// camelCase on the wire (the serializer in Program.cs uses
// JsonNamingPolicy.CamelCase). NO SolidColorBrush — every color is a plain
// *Hex string the Tauri frontend resolves itself.
//
// READ-ONLY: this snapshot only lists the user's rules + their live RULE CHECK
// state. Add/edit/toggle/delete are WRITE operations and are DEFERRED — the
// frontend shows a small "coming soon" note for Add (see AddComingSoon below).
// The violation check (CheckViolationsAsync) and behavioral evidence
// (GetRuleEvidenceAsync) are READS and ARE wired here.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level rules snapshot returned by GET /api/rules.</summary>
public sealed record RulesDto(
    string GeneratedAt,
    // True when the user has no rules at all (drive the empty state).
    bool IsEmpty,
    string EmptyMessage,
    // Total rules across both buckets.
    int TotalCount,
    int ActiveCount,
    int InactiveCount,
    // Active rules first, then inactive — each list is display-ready.
    IReadOnlyList<RuleRowDto> ActiveRules,
    IReadOnlyList<RuleRowDto> InactiveRules,
    // ── RULE CHECK banner (live check vs today's games) ─────────────────────
    // True when any active rule is tripped today; gates the 'RULE CHECK' banner.
    bool HasViolations,
    // One entry per tripped active rule. Empty when nothing tripped.
    IReadOnlyList<ViolationBannerItemDto> Violations,
    // Add/edit are WRITE ops, deferred. Frontend shows this as a coming-soon note.
    bool AddComingSoon,
    string AddComingSoonNote);

/// <summary>
/// One entry in the live 'RULE CHECK' banner. Mirrors
/// Revu.App.ViewModels.ViolationBannerItem. When the tripped rule carries a P2c
/// replacement plan, <see cref="HasPlan"/> is true and the frontend leads with
/// the IF/THEN block (<see cref="ConditionCue"/> + <see cref="ReplacementPlan"/>)
/// demoting RuleName/Reason to a subhead; otherwise it shows "RuleName — Reason".
/// </summary>
public sealed record ViolationBannerItemDto(
    // The tripped rule's name.
    string RuleName,
    // Why it tripped today (RuleViolation.Reason), e.g. "Played 6 games today (max 5)".
    string Reason,
    // P2c player-authored "then I will…" plan; may be empty.
    string ReplacementPlan,
    // The rule's own IF leg (its ConditionText), so the player never re-types it.
    string ConditionCue,
    // True when a non-empty ReplacementPlan exists (selects the plan-led layout).
    bool HasPlan);

/// <summary>
/// One rule row. Mirrors Revu.App.ViewModels.RuleDisplayItem's display surface
/// (TypeBadge → TypeBadge, ConditionText → ConditionText) so the Tauri frontend
/// renders the same glass row the WinUI app shows — minus the violation/evidence
/// state, which is out of scope for the read-only list. Emits an AccentHex color
/// string, never a brush.
/// </summary>
public sealed record RuleRowDto(
    long Id,
    // Rule name / title (RuleRecord.Name).
    string Name,
    // Free-text description / rationale (RuleRecord.Description); may be empty.
    string Description,
    bool HasDescription,
    // Raw rule-type key: custom | no_play_day | no_play_after | loss_streak | max_games | min_mental.
    string RuleType,
    // Display badge for the type, e.g. "NO-PLAY DAY" (mirror RuleDisplayItem.TypeBadge).
    string TypeBadge,
    // Raw condition value as stored (RuleRecord.ConditionValue); may be empty.
    string ConditionValue,
    // Human-readable condition sentence, e.g. "Max 5 games per day" (mirror RuleDisplayItem.ConditionText).
    string ConditionText,
    bool HasCondition,
    // P2c: player-authored "then I will…" replacement plan (display-only); may be empty.
    string ReplacementPlan,
    bool HasReplacementPlan,
    bool Enabled,
    // "Active" / "Disabled" — display-ready state label.
    string StateText,
    bool IsCustom,
    // Per-row accent: custom rules are neutral, typed rules use the accent.
    string AccentHex,
    // ── Live RULE CHECK state (mirror RuleDisplayItem) ──────────────────────
    // True when this rule tripped vs today's games. Drives the TRIPPED badge.
    bool IsViolated,
    // Why it tripped (empty unless IsViolated).
    string ViolationReason,
    bool HasViolationReason,
    // True when the rule was checked AND not tripped (non-custom only). Drives
    // the OK badge. Custom rules are never checked → neither badge shows.
    bool IsOk,
    // ── P2b behavioral evidence line (mirror RuleDisplayItem.EvidenceLine) ──
    // The rule's own historical record, computed behaviorally. Empty for custom
    // rules and when the evidence query fails (best-effort). e.g.
    // "TRIPPED 3× (1W–2L) · BASELINE WR 52% · LAST 2026-06-10".
    string EvidenceLine,
    bool HasEvidenceLine);

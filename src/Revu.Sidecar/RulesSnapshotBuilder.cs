#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only rules snapshot served at GET /api/rules.
///
/// <para>
/// Lists the user's gaming rules from <see cref="IRulesRepository.GetAllAsync"/>,
/// split into active/inactive buckets, and emits the camelCase JSON contract
/// (see <see cref="RulesDto"/> and desktop/ui/sample-rules.json). It reproduces
/// the WinUI <c>RulesViewModel.RefreshDataAsync</c> EXACTLY: per-rule display
/// formatting (TypeBadge + per-type ConditionText), the live 'RULE CHECK'
/// violation banner (checked vs today's games), and each rule's behavioral
/// evidence line (P2b) — so the Tauri frontend renders the same glass rows.
/// </para>
///
/// <para>
/// This is the READ half of the page: add / edit / toggle / delete are WRITE
/// operations served by the <c>POST /api/rule/*</c> endpoints (the frontend
/// refetches this snapshot after each). The DTO's <c>AddComingSoon</c> flag is
/// now always false. BOTH the violation check
/// (<see cref="IRulesRepository.CheckViolationsAsync"/>) and the evidence query
/// (<see cref="IRulesRepository.GetRuleEvidenceAsync"/>) are READS and ARE
/// wired here. The whole load is wrapped in try/catch that degrades to an empty
/// list so a failing query never blanks the page; the evidence query is
/// additionally best-effort (its own try/catch) per the WinUI VM.
/// </para>
///
/// <para>
/// COLOR PARITY: like DashboardSnapshotBuilder, hex constants are hardcoded from
/// the glass-aurora mockup palette (accent #9d8bff for typed rules, neutral
/// slate for custom). NO SolidColorBrush — only *Hex strings cross the wire.
/// </para>
/// </summary>
public sealed class RulesSnapshotBuilder
{
    // ── Mockup palette (mirror DashboardSnapshotBuilder; TODO: extract to Core) ─
    private const string AccentHex = "#9d8bff";
    private const string NeutralHex = "#7B8494";

    private readonly IRulesRepository _rulesRepo;
    // Today's games drive the live violation check (mirror RulesViewModel's
    // IGameRepository dependency). GetTodaysGamesAsync is a READ.
    private readonly IGameRepository _gameRepo;
    private readonly ILogger<RulesSnapshotBuilder> _logger;

    public RulesSnapshotBuilder(
        IRulesRepository rulesRepo,
        IGameRepository gameRepo,
        ILogger<RulesSnapshotBuilder> logger)
    {
        _rulesRepo = rulesRepo;
        _gameRepo = gameRepo;
        _logger = logger;
    }

    public async Task<RulesDto> BuildAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;

        var active = new List<RuleRowDto>();
        var inactive = new List<RuleRowDto>();
        var violations = new List<ViolationBannerItemDto>();

        try
        {
            var rules = await _rulesRepo.GetAllAsync();

            // ── Live violation check vs today's games (mirror RefreshDataAsync) ─
            // mentalRating is omitted (null) on this page — min_mental rules are
            // therefore never live-violated here, exactly like the WinUI VM. The
            // whole check is guarded so a failing games/violation query just
            // yields no banner; the rule list still renders.
            var violationMap = await BuildViolationMapAsync();

            // ── P2b behavioral evidence (best-effort) ───────────────────────
            var evidenceMap = await BuildEvidenceMapAsync(rules);

            foreach (var rule in rules)
            {
                violationMap.TryGetValue(rule.Id, out var violation);
                evidenceMap.TryGetValue(rule.Id, out var evidence);

                var row = MapRule(rule, violation, evidence);
                if (rule.IsActive)
                {
                    active.Add(row);
                    if (row.IsViolated)
                    {
                        // P2c: the banner leads with the plan when one exists;
                        // the cue is the rule's own IF leg, so the player never
                        // re-types it (mirror ViolationBannerItem).
                        violations.Add(new ViolationBannerItemDto(
                            RuleName: rule.Name,
                            Reason: row.ViolationReason,
                            ReplacementPlan: row.ReplacementPlan,
                            ConditionCue: row.ConditionText,
                            HasPlan: row.HasReplacementPlan));
                    }
                }
                else
                {
                    inactive.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rules: rule list load failed");
        }

        var total = active.Count + inactive.Count;

        return new RulesDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            IsEmpty: total == 0,
            EmptyMessage: "No rules yet. Add guardrails in the desktop app to stay disciplined.",
            TotalCount: total,
            ActiveCount: active.Count,
            InactiveCount: inactive.Count,
            ActiveRules: active,
            InactiveRules: inactive,
            HasViolations: violations.Count > 0,
            Violations: violations,
            // Rules CRUD is now fully wired (create / update / toggle / delete via
            // the /api/rule/* endpoints), so the old read-only "coming soon" note
            // is gone. Kept in the contract (false) for wire back-compat.
            AddComingSoon: false,
            AddComingSoonNote: "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Violation check (mirror RulesViewModel.RefreshDataAsync game mapping)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<long, RuleViolation>> BuildViolationMapAsync()
    {
        var map = new Dictionary<long, RuleViolation>();
        try
        {
            var todaysGames = await _gameRepo.GetTodaysGamesAsync();
            var games = todaysGames
                .Select(g => new RuleCheckGame(
                    GameId: g.GameId,
                    Win: g.Win,
                    ChampionName: g.ChampionName,
                    Timestamp: g.Timestamp))
                .ToList();

            // mentalRating null → min_mental rules never live-trip here (VM parity).
            var violations = await _rulesRepo.CheckViolationsAsync(games);
            foreach (var v in violations)
            {
                map[v.Rule.Id] = v;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rules: live violation check failed");
        }
        return map;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // P2b behavioral evidence (best-effort — mirror the VM's inner try/catch)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyDictionary<long, RuleEvidence>> BuildEvidenceMapAsync(
        IReadOnlyList<RuleRecord> rules)
    {
        try
        {
            return await _rulesRepo.GetRuleEvidenceAsync(rules);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rules: behavioral evidence load failed (degrading)");
            return new Dictionary<long, RuleEvidence>();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule mapping (mirror Revu.App.ViewModels.RuleDisplayItem)
    // ─────────────────────────────────────────────────────────────────────────

    private static RuleRowDto MapRule(RuleRecord rule, RuleViolation? violation, RuleEvidence? evidence)
    {
        var isCustom = rule.RuleType == "custom";
        var typeBadge = TypeBadge(rule.RuleType);
        var conditionText = ConditionText(rule.RuleType, rule.ConditionValue);
        var hasDescription = !string.IsNullOrWhiteSpace(rule.Description);
        var hasCondition = !string.IsNullOrWhiteSpace(conditionText);
        var hasPlan = !string.IsNullOrWhiteSpace(rule.ReplacementPlan);

        // Mirror RuleDisplayItem: IsChecked = violation present AND non-custom;
        // IsOk = checked and not violated. Custom rules are never checked.
        var isViolated = violation?.Violated ?? false;
        var violationReason = violation?.Reason ?? "";
        var isChecked = violation is not null && !isCustom;
        var isOk = isChecked && !isViolated;
        var hasViolationReason = isViolated && !string.IsNullOrWhiteSpace(violationReason);

        var evidenceLine = BuildEvidenceLine(evidence);
        var hasEvidenceLine = !string.IsNullOrWhiteSpace(evidenceLine);

        return new RuleRowDto(
            Id: rule.Id,
            Name: rule.Name,
            Description: rule.Description,
            HasDescription: hasDescription,
            RuleType: rule.RuleType,
            TypeBadge: typeBadge,
            ConditionValue: rule.ConditionValue,
            ConditionText: conditionText,
            HasCondition: hasCondition,
            ReplacementPlan: rule.ReplacementPlan,
            HasReplacementPlan: hasPlan,
            Enabled: rule.IsActive,
            StateText: rule.IsActive ? "Active" : "Disabled",
            IsCustom: isCustom,
            AccentHex: isCustom ? NeutralHex : AccentHex,
            IsViolated: isViolated,
            ViolationReason: violationReason,
            HasViolationReason: hasViolationReason,
            IsOk: isOk,
            EvidenceLine: evidenceLine,
            HasEvidenceLine: hasEvidenceLine);
    }

    /// <summary>
    /// Mirror of RulesViewModel.BuildEvidenceLine: neutral, n-honest record line
    /// — exact W–L below 10 trips, percentages from 10 up. Empty when no record
    /// (custom rules / evidence query failed).
    /// </summary>
    private static string BuildEvidenceLine(RuleEvidence? evidence)
    {
        if (evidence is null) return "";
        if (evidence.TriggerGames == 0) return "NO TRIPS ON RECORD";

        var baselinePct = evidence.BaselineGames > 0
            ? (int)Math.Round(100.0 * evidence.BaselineWins / evidence.BaselineGames)
            : 0;

        if (evidence.TriggerGames < 10)
        {
            var losses = evidence.TriggerGames - evidence.TriggerWins;
            return $"TRIPPED {evidence.TriggerGames}× ({evidence.TriggerWins}W–{losses}L) · BASELINE WR {baselinePct}% · LAST {evidence.LastTriggerDate}";
        }

        var trippedPct = (int)Math.Round(100.0 * evidence.TriggerWins / evidence.TriggerGames);
        return $"TRIPPED {evidence.TriggerGames}× · WR WHEN TRIPPED {trippedPct}% VS {baselinePct}% BASELINE · LAST {evidence.LastTriggerDate}";
    }

    /// <summary>Mirror of RuleDisplayItem.TypeBadge.</summary>
    private static string TypeBadge(string ruleType) => ruleType switch
    {
        "custom" => "CUSTOM",
        "no_play_day" => "NO-PLAY DAY",
        "no_play_after" => "NO PLAY AFTER",
        "loss_streak" => "LOSS STREAK",
        "max_games" => "MAX GAMES/DAY",
        "min_mental" => "MINIMUM MENTAL",
        _ => "CUSTOM"
    };

    /// <summary>Mirror of RuleDisplayItem.ConditionText.</summary>
    private static string ConditionText(string ruleType, string conditionValue)
    {
        if (string.IsNullOrEmpty(conditionValue)) return "";
        return ruleType switch
        {
            "no_play_day" => $"Days: {conditionValue}",
            "no_play_after" => FormatHour(conditionValue),
            "loss_streak" => FormatLossStreak(conditionValue),
            "max_games" => $"Max {conditionValue} games per day",
            "min_mental" => $"Don't queue below mental {conditionValue}",
            _ => ""
        };
    }

    /// <summary>Mirror of RuleDisplayItem.FormatHour.</summary>
    private static string FormatHour(string value)
    {
        if (!int.TryParse(value, out var h)) return "";
        var suffix = h < 12 ? "AM" : "PM";
        var displayH = h <= 12 ? h : h - 12;
        if (displayH == 0) displayH = 12;
        return $"No play after {displayH}:00 {suffix}";
    }

    /// <summary>Mirror of RuleDisplayItem.FormatLossStreak (reuses the Core parser).</summary>
    private static string FormatLossStreak(string value)
    {
        var (threshold, cd) = RulesRepository.ParseLossStreakCondition(value);
        if (threshold <= 0) return "";
        if (cd is not int m || m <= 0) return $"Stop after {threshold} consecutive losses";
        var window = m >= 60 ? $"{m / 60}h{(m % 60 > 0 ? $" {m % 60}m" : "")}" : $"{m}m";
        return $"Stop after {threshold} losses (unlock after {window})";
    }
}

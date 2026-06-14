#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>Result of checking a single rule for violations.</summary>
public sealed record RuleViolation(
    RuleRecord Rule,
    bool Violated,
    string Reason);

/// <summary>
/// P2b (digest 2026-06-12): a rule's own historical record, computed
/// BEHAVIORALLY from game outcomes/times — never from session_log.rule_broken,
/// which is clearing-censored (measures flags the user let stand, not
/// violations; METHODOLOGY tension #8). "Trigger games" are games played while
/// the rule's condition already held (e.g. queued after 2 consecutive losses).
/// </summary>
public sealed record RuleEvidence(
    long RuleId,
    int TriggerGames,
    int TriggerWins,
    string LastTriggerDate,
    int BaselineGames,
    int BaselineWins);

/// <summary>CRUD + violation checking for user-defined gaming rules.</summary>
public interface IRulesRepository
{
    Task<long> CreateAsync(string name, string description = "", string ruleType = "custom",
        string conditionValue = "", string replacementPlan = "");

    Task UpdateAsync(long ruleId, string name, string description, string ruleType, string conditionValue,
        string replacementPlan = "");

    Task<IReadOnlyList<RuleRecord>> GetAllAsync();

    Task<IReadOnlyList<RuleRecord>> GetActiveAsync();

    Task<RuleRecord?> GetAsync(long ruleId);

    Task ToggleAsync(long ruleId);

    Task DeleteAsync(long ruleId);

    /// <summary>
    /// Check all active rules and return violation results.
    /// Rule types: no_play_day, no_play_after, loss_streak, max_games, min_mental, custom.
    /// </summary>
    Task<IReadOnlyList<RuleViolation>> CheckViolationsAsync(
        IReadOnlyList<RuleCheckGame>? todaysGames = null,
        int? mentalRating = null);

    /// <summary>
    /// Per-rule behavioral record over all visible games (see
    /// <see cref="RuleEvidence"/>). Custom rules have no automated record and
    /// are omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<long, RuleEvidence>> GetRuleEvidenceAsync(
        IReadOnlyList<RuleRecord> rules);

    /// <summary>
    /// Consecutive most-recent play-days containing zero behavioral rule
    /// trips. Replaces the rule_broken-based streak (clearing-censored):
    /// flag housekeeping can neither fake nor break this number. Days before
    /// the first rule existed are out of scope, and a rule only judges games
    /// played after its creation. 0 when no non-custom active rules exist.
    /// Trips on is_skipped games are streak-neutral — skip is the player's
    /// deliberate streak-protection lever (the streak is a motivation
    /// mechanic; <see cref="GetRuleEvidenceAsync"/> records stay unforgiving).
    /// Days before the behavioral re-base epoch (2026-06-12) keep their
    /// flag-era verdicts (surviving non-skipped rule_broken flags) so the
    /// re-base never retroactively erases an earned streak.
    /// <paramref name="behavioralSinceDate"/> overrides the epoch (tests).
    /// </summary>
    Task<int> GetBehavioralAdherenceStreakAsync(string? behavioralSinceDate = null);
}

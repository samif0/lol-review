#nullable enable

using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

/// <summary>hard_stops.action values. Persisted — never rename.</summary>
public static class HardStopActions
{
    /// <summary>The enforcer left the matchmaking queue on the player's behalf.</summary>
    public const string CancelledQueue = "cancelled_queue";

    /// <summary>A match popped inside the poll window; the enforcer declined it.</summary>
    public const string DeclinedReadyCheck = "declined_ready_check";

    /// <summary>The player explicitly overrode the rule for the rest of the day.</summary>
    public const string Override = "override";
}

/// <summary>
/// One enforced trip: the rule that holds, the live reason
/// (<see cref="RuleViolation.Reason"/>), and — when the rule's own condition
/// implies one — the unix second it stops holding, for the countdown.
/// </summary>
public sealed record HardStopDecision(RuleRecord Rule, string Reason, long? UnlockAt);

/// <summary>
/// v3.7: the hard-stop decision, pure and DB-free. The sidecar feeds it the live
/// violation check (<see cref="IRulesRepository.CheckViolationsAsync"/>), the
/// rule ids overridden today, and today's games; it answers "cancel the queue
/// for THIS rule" or null.
///
/// <para>
/// WHY THIS EXISTS. The Rules page already computes trips, but a trip was only
/// ever a label. The hard part is acting on the label in the moment — the
/// decision to stop has to be made in advance and executed by something that
/// does not change its mind at the moment of the next queue. That is the app. So a
/// rule flagged <c>enforce</c> is not advice; while it holds, the sidecar cancels
/// the League client's own queue (the same call the client's Cancel button
/// makes) and shows the player the plan they wrote for exactly this moment.
/// </para>
///
/// <para>
/// Deliberately narrow: only rule types whose condition replays from game
/// history can be enforced (<see cref="CanEnforce"/>). <c>custom</c> has no
/// condition, and <c>min_mental</c> needs a live mental rating the queue path
/// never has (every game is logged at the default until reviewed), so neither
/// is ever enforced — flagging them would produce a rule that can never hold or
/// one that trips on stale data.
/// </para>
/// </summary>
public static class HardStopPolicy
{
    /// <summary>Rule types the enforcer can act on. Mirrors the behavioral
    /// reconstruction set in RulesRepository.FindTriggerGames minus min_mental.</summary>
    public static bool CanEnforce(string? ruleType) =>
        ruleType is "loss_streak" or "max_games" or "no_play_after" or "no_play_day";

    /// <summary>
    /// The first tripped rule that is active, flagged enforce, enforceable by type
    /// and not overridden today — or null when the queue may proceed.
    /// <paramref name="violations"/> is the live check's full result (tripped and
    /// clean rows alike); order is the repository's (creation order).
    /// </summary>
    public static HardStopDecision? Decide(
        IReadOnlyList<RuleViolation> violations,
        IReadOnlySet<long> overriddenRuleIds,
        IReadOnlyList<RuleCheckGame> todaysGames,
        DateTimeOffset now)
    {
        foreach (var violation in violations)
        {
            if (!violation.Violated) continue;
            var rule = violation.Rule;
            if (!rule.IsActive || !rule.Enforce || !CanEnforce(rule.RuleType)) continue;
            if (overriddenRuleIds.Contains(rule.Id)) continue;

            return new HardStopDecision(rule, violation.Reason, UnlockAtFor(rule, todaysGames, now));
        }

        return null;
    }

    /// <summary>
    /// When the rule stops holding, as a unix second, or null when its type has
    /// no natural end. loss_streak WITH a cooldown ends when the cooldown armed by
    /// the loss that first crossed the threshold expires (mirrors
    /// CheckViolationsAsync exactly); everything else — a day cap, a curfew hour,
    /// a no-play day, a rest-of-day loss streak — resets at local midnight.
    /// </summary>
    public static long? UnlockAtFor(RuleRecord rule, IReadOnlyList<RuleCheckGame> todaysGames, DateTimeOffset now)
    {
        switch (rule.RuleType)
        {
            case "loss_streak":
            {
                var (threshold, cooldownMinutes) = RulesRepository.ParseLossStreakCondition(rule.ConditionValue);
                if (threshold > 0 && cooldownMinutes is int cd && cd > 0 && todaysGames.Count > 0)
                {
                    var streakStartIndex = todaysGames.Count;
                    for (var i = todaysGames.Count - 1; i >= 0; i--)
                    {
                        if (todaysGames[i].Win) break;
                        streakStartIndex = i;
                    }

                    var consecutive = todaysGames.Count - streakStartIndex;
                    if (consecutive >= threshold)
                    {
                        var triggerTs = todaysGames[streakStartIndex + threshold - 1].Timestamp;
                        if (triggerTs > 0) return triggerTs + cd * 60L;
                    }
                }

                return NextLocalMidnight(now);
            }

            case "max_games":
            case "no_play_after":
            case "no_play_day":
                return NextLocalMidnight(now);

            default:
                return null;
        }
    }

    /// <summary>The next local midnight after <paramref name="now"/>, as a unix
    /// second. "Today" everywhere in the rules code is the machine's local day
    /// (GameRepository.GetTodaysGamesAsync uses DateTime.Today), so the reset
    /// instant must be local too.</summary>
    public static long NextLocalMidnight(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var tomorrow = local.Date.AddDays(1);
        var offset = TimeZoneInfo.Local.GetUtcOffset(tomorrow);
        return new DateTimeOffset(tomorrow, offset).ToUnixTimeSeconds();
    }

    /// <summary>Local midnight that started the day containing <paramref name="now"/>,
    /// as a unix second — the "since" bound for today's overrides and holds.</summary>
    public static long StartOfLocalDay(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var today = local.Date;
        var offset = TimeZoneInfo.Local.GetUtcOffset(today);
        return new DateTimeOffset(today, offset).ToUnixTimeSeconds();
    }
}

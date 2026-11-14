#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// v3.7: the hard stop. Runs on every <see cref="QueueDetectedMessage"/> (the
/// monitor reports Matchmaking / ReadyCheck each tick they hold) and, when an
/// active rule flagged <c>enforce</c> is tripped, leaves the League client's own
/// queue — the same request its Cancel button sends — records the intervention,
/// and pushes a <c>hardStop</c> SSE event so the shell can show the player the
/// plan they wrote for this moment.
///
/// <para>
/// A match that has already been found is never answered. Accepting or declining
/// it affects the other players in that lobby, so that choice stays with the
/// player: in <see cref="GamePhase.ReadyCheck"/> the enforcer makes no request
/// that changes the client, records nothing, and only publishes the rule and the
/// plan as a notice (<see cref="HardStopActions.MatchFound"/>), once per match.
/// </para>
///
/// <para>
/// The point is that nothing is asked of the player in the moment. The rule was
/// chosen in advance; the app holds it. A tripped rule without <c>enforce</c>
/// still behaves exactly as before (a label on the Rules page).
/// </para>
///
/// <para>
/// Scope and safety: acts only on the user's own client through the LCU Revu
/// already talks to; reads no game data; never answers a found match and never
/// dodges a champ select (once a match is found the stop is over for this
/// game), and never touches a queue that is not ranked (ARAM, normals
/// and other casual modes always proceed). Every decision comes from <see cref="HardStopPolicy"/> (pure)
/// over the same live check the Rules page runs. An override written through
/// <see cref="OverrideAsync"/> silences the rule for the rest of the local day.
/// Best-effort throughout: any failure logs and lets the queue proceed.
/// </para>
/// </summary>
public sealed class HardStopEnforcer
{
    /// <summary>Two ticks inside this window act once. The monitor polls at 5s
    /// and the LCU drops back to Lobby right after a cancel, so this only
    /// matters when the cancel call itself was slow.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    /// <summary>The monitor reports a found match on every tick it stays on screen.
    /// One notice per match: later ticks inside this window are the same match. A
    /// tick that sees the client searching again ends the window early.</summary>
    private static readonly TimeSpan MatchFoundNoticeWindow = TimeSpan.FromSeconds(30);

    private readonly IRulesRepository _rules;
    private readonly IGameRepository _games;
    private readonly IHardStopsRepository _hardStops;
    private readonly ILcuClient _lcu;
    private readonly SidecarEventHub _eventHub;
    private readonly LcuLiveState _liveState;
    private readonly ILogger<HardStopEnforcer> _logger;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _gate = new();
    private DateTimeOffset _lastActionAt = DateTimeOffset.MinValue;
    private DateTimeOffset _matchFoundNoticeAt = DateTimeOffset.MinValue;
    private bool _busy;

    public HardStopEnforcer(
        IRulesRepository rules,
        IGameRepository games,
        IHardStopsRepository hardStops,
        ILcuClient lcu,
        SidecarEventHub eventHub,
        LcuLiveState liveState,
        ILogger<HardStopEnforcer> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _rules = rules;
        _games = games;
        _hardStops = hardStops;
        _lcu = lcu;
        _eventHub = eventHub;
        _liveState = liveState;
        _logger = logger;
        _clock = clock ?? (static () => DateTimeOffset.Now);
    }

    /// <summary>
    /// Evaluate the enforced rules against today's games and, if one holds, act.
    /// Returns the snapshot that was published, or null when the queue may proceed
    /// (nothing enforced, nothing tripped, overridden today, debounced, or the LCU
    /// rejected the cancel). While the client is searching, acting means cancelling
    /// the search. Once a match is found, acting means showing the notice and
    /// nothing else.
    /// </summary>
    public async Task<HardStopSnapshot?> HandleQueueAsync(GamePhase phase, CancellationToken ct = default)
    {
        if (phase is not (GamePhase.Matchmaking or GamePhase.ReadyCheck)) return null;
        var matchFound = phase == GamePhase.ReadyCheck;

        lock (_gate)
        {
            // Searching again: the next found match is a new one and gets its own notice.
            if (!matchFound) _matchFoundNoticeAt = DateTimeOffset.MinValue;
            if (_busy) return null;
            var tick = _clock();
            if (tick - _lastActionAt < Debounce) return null;
            if (matchFound && tick - _matchFoundNoticeAt < MatchFoundNoticeWindow) return null;
            _busy = true;
        }

        try
        {
            var decision = await DecideAsync(ct).ConfigureAwait(false);
            if (decision is null) return null;

            // Rules are for ranked. ARAM and normals always proceed, and so does a
            // queue the LCU cannot name (-1): the next tick asks again.
            var queueId = await _lcu.GetLobbyQueueIdAsync(ct).ConfigureAwait(false);
            if (!HardStopPolicy.IsRankedQueue(queueId))
            {
                _logger.LogInformation("Hard stop: rule {RuleId} holds but queue {QueueId} is not ranked; letting it proceed",
                    decision.Rule.Id, queueId);
                return null;
            }

            if (matchFound)
            {
                // The match is the player's to answer. No request goes to the client
                // and nothing is recorded, because nothing was held.
                var foundAt = _clock();
                lock (_gate) _matchFoundNoticeAt = foundAt;
                var notice = BuildSnapshot(decision, HardStopActions.MatchFound, foundAt);
                _liveState.SetHardStop(notice);
                _eventHub.Publish("hardStop", notice);
                _logger.LogInformation("Hard stop: a match was found while rule '{Rule}' holds ({Reason}); left for the player to answer",
                    decision.Rule.Name, decision.Reason);
                return notice;
            }

            const string action = HardStopActions.CancelledQueue;
            var ok = await _lcu.CancelMatchmakingAsync(ct).ConfigureAwait(false);
            if (!ok)
            {
                // Already out of queue: nothing to hold.
                _logger.LogInformation("Hard stop: LCU rejected {Action} for rule {RuleId} ({Reason})",
                    action, decision.Rule.Id, decision.Reason);
                return null;
            }

            var now = _clock();
            lock (_gate) _lastActionAt = now;

            var snapshot = BuildSnapshot(decision, action, now);

            try
            {
                await _hardStops.RecordAsync(decision.Rule.Id, action, decision.Reason, snapshot.At).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The stop already happened; a missing ledger row must not hide it.
                _logger.LogWarning(ex, "Hard stop: could not record the intervention for rule {RuleId}", decision.Rule.Id);
            }

            _liveState.SetHardStop(snapshot);
            _eventHub.Publish("hardStop", snapshot);
            _logger.LogInformation("Hard stop: {Action} — rule '{Rule}' ({Reason})",
                action, decision.Rule.Name, decision.Reason);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hard stop: evaluation failed — letting the queue proceed");
            return null;
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    private static HardStopSnapshot BuildSnapshot(HardStopDecision decision, string action, DateTimeOffset at) =>
        new(
            RuleId: decision.Rule.Id,
            RuleName: decision.Rule.Name,
            Reason: decision.Reason,
            ConditionCue: RulesSnapshotBuilder.ConditionText(decision.Rule.RuleType, decision.Rule.ConditionValue),
            ReplacementPlan: decision.Rule.ReplacementPlan,
            HasPlan: !string.IsNullOrWhiteSpace(decision.Rule.ReplacementPlan),
            Action: action,
            UnlockAt: decision.UnlockAt,
            At: at.ToUnixTimeSeconds());

    /// <summary>
    /// The player accepted the found match and champ select began. The notice was
    /// about that one match, so it goes away. A lock from a cancelled search is a
    /// different thing and stays until it is overridden or stops holding.
    /// </summary>
    public void EndMatchFoundNotice()
    {
        var current = _liveState.HardStop;
        if (current is null || current.Action != HardStopActions.MatchFound) return;
        _liveState.SetHardStop(null);
        _eventHub.Publish("hardStopCleared", new { ruleId = current.RuleId });
        _logger.LogInformation("Hard stop: the found match was accepted; notice for rule {RuleId} cleared", current.RuleId);
    }

    /// <summary>
    /// The player chose to queue anyway. Records the override (it silences the
    /// rule for the rest of the local day — see <see cref="DecideAsync"/>) and
    /// clears the replayed snapshot so a reload no longer shows the lock.
    /// </summary>
    public async Task OverrideAsync(long ruleId, CancellationToken ct = default)
    {
        await _hardStops.RecordAsync(ruleId, HardStopActions.Override, "", _clock().ToUnixTimeSeconds()).ConfigureAwait(false);
        var current = _liveState.HardStop;
        if (current is not null && current.RuleId == ruleId)
        {
            _liveState.SetHardStop(null);
        }
        _eventHub.Publish("hardStopOverridden", new { ruleId });
        _logger.LogInformation("Hard stop: rule {RuleId} overridden for the rest of the day", ruleId);
    }

    /// <summary>
    /// 3.14.1: a game's remake verdict changed (startup check, Match-V5 confirmation, or
    /// the player's toggle). When the lock that is showing no longer holds for the same
    /// rule, clear it and tell the shell, so a player blocked by a remake can queue.
    /// </summary>
    public async Task ReevaluateAsync(CancellationToken ct = default)
    {
        var current = _liveState.HardStop;
        if (current is null) return;
        HardStopDecision? decision;
        try
        {
            decision = await DecideAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Hard stop: re-evaluation failed; keeping the current lock");
            return;
        }
        if (decision is not null && decision.Rule.Id == current.RuleId) return;

        _liveState.SetHardStop(null);
        _eventHub.Publish("hardStopCleared", new { ruleId = current.RuleId });
        _logger.LogInformation("Hard stop: rule {RuleId} no longer holds after a remake change; lock cleared", current.RuleId);
    }

    /// <summary>The pure decision over live data. Fast-exits before any game
    /// query when no active rule is flagged enforce (the common case).</summary>
    public async Task<HardStopDecision?> DecideAsync(CancellationToken ct = default)
    {
        var active = await _rules.GetActiveAsync().ConfigureAwait(false);
        if (!active.Any(r => r.Enforce && HardStopPolicy.CanEnforce(r.RuleType))) return null;

        var now = _clock();
        // 3.14.1: remakes are not counted (GetTodaysRuleGamesAsync drops them).
        var todaysGames = await _games.GetTodaysRuleGamesAsync().ConfigureAwait(false);

        // mentalRating null: min_mental is never enforced (see HardStopPolicy).
        var violations = await _rules.CheckViolationsAsync(todaysGames, mentalRating: null).ConfigureAwait(false);
        var overridden = await _hardStops.GetOverriddenRuleIdsAsync(HardStopPolicy.StartOfLocalDay(now)).ConfigureAwait(false);

        return HardStopPolicy.Decide(violations, overridden, todaysGames, now);
    }
}

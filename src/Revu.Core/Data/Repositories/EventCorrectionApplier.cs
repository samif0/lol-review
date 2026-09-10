#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Data.Repositories;

public enum IncomingAction { Insert, Suppress, InsertPatched }

/// <summary>One decision per incoming row. Row is the original object for Insert, a patched clone for
/// InsertPatched. CorrectionRowId/StateAfterInsert are set only when a ledger row must learn the new id.</summary>
public sealed record IncomingDecision(int Index, IncomingAction Action, GameEvent Row, string EventKey,
    long? CorrectionRowId, string? StateAfterInsert);

/// <summary>A row to insert that is not in the batch (a missing op add row).</summary>
public sealed record ExtraInsert(GameEvent Row, string EventKey, long CorrectionRowId, string StateAfterInsert);

/// <summary>What a game_events writer does with one incoming batch once the ledger has had its say.</summary>
public sealed class ReconcilePlan
{
    internal ReconcilePlan(IReadOnlyList<IncomingDecision> decisions, IReadOnlyList<ExtraInsert> extras,
        int suppressed, int absorbed, int orphaned, int reapplied)
    {
        Decisions = decisions;
        Extras = extras;
        Suppressed = suppressed;
        Absorbed = absorbed;
        Orphaned = orphaned;
        Reapplied = reapplied;
    }

    /// <summary>Aligned with the incoming batch.</summary>
    public IReadOnlyList<IncomingDecision> Decisions { get; }
    public IReadOnlyList<ExtraInsert> Extras { get; }
    public int Suppressed { get; }
    public int Absorbed { get; }
    public int Orphaned { get; }
    public int Reapplied { get; }

    /// <summary>Call after inserting a decision whose CorrectionRowId is set: writes state + applied_event_id.</summary>
    public Task RecordInsertedAsync(SqliteConnection conn, SqliteTransaction tx, IncomingDecision d, long newRowId) =>
        d.CorrectionRowId is { } id
            ? EventCorrectionSql.SetStateAsync(conn, tx, id, d.StateAfterInsert ?? CorrectionStates.Active, newRowId)
            : Task.CompletedTask;

    /// <summary>Call after inserting an extra: writes state + applied_event_id.</summary>
    public Task RecordInsertedAsync(SqliteConnection conn, SqliteTransaction tx, ExtraInsert x, long newRowId) =>
        EventCorrectionSql.SetStateAsync(conn, tx, x.CorrectionRowId, x.StateAfterInsert, newRowId);

    /// <summary>Legacy behaviour: every incoming row is inserted as it is.</summary>
    public static ReconcilePlan PassThrough(IReadOnlyList<GameEvent> incoming, IReadOnlyList<string> keys)
    {
        var decisions = new IncomingDecision[incoming.Count];
        for (var i = 0; i < incoming.Count; i++)
            decisions[i] = new IncomingDecision(i, IncomingAction.Insert, incoming[i], KeyAt(keys, incoming, i), null, null);
        return new ReconcilePlan(decisions, [], 0, 0, 0, 0);
    }

    internal static string KeyAt(IReadOnlyList<string> keys, IReadOnlyList<GameEvent> incoming, int i) =>
        i < keys.Count ? keys[i] : EventIdentity.KeyFor(incoming[i]);
}

/// <summary>
/// Rules A, B, C and the second half of G of the corrections ledger: the piece every game_events
/// writer runs INSIDE its own connection and transaction so a correction survives the detector
/// that produced its subject re-running. Never opens a connection, never commits and never throws
/// out: a schema-less database degrades to the legacy plan, a failing correction records its
/// error in apply_error and the batch goes on (a capture save never fails because of a fix).
/// </summary>
public static partial class EventCorrectionApplier
{
    /// <summary>Every event type the capture-time batch can carry (rule A scope): all catalog types plus
    /// FLASH, SUMMONER_SPELL, LEVEL_UP, minus JUNGLE_PROXIMITY and TEAMFIGHT.</summary>
    public static readonly IReadOnlySet<string> LiveTypes = new HashSet<string>(
        EventCorrectionCatalog.Types.Select(t => t.Type)
            .Where(t => t != GameEvent.EventTypes.JungleProximity && t != GameEvent.TrackableTokens.TeamfightToken)
            .Concat([GameEvent.EventTypes.Flash, GameEvent.EventTypes.SummonerSpell, GameEvent.EventTypes.LevelUp]),
        StringComparer.Ordinal);

    /// <summary>Rules A and B. Call AFTER the caller's delete and BEFORE its inserts, inside its transaction.
    /// <paramref name="scope"/> = the event types this batch re-detects (A: LiveTypes; B: distinct types in the batch).</summary>
    public static async Task<ReconcilePlan> PlanIncomingAsync(SqliteConnection conn, SqliteTransaction tx, long gameId,
        IReadOnlyList<GameEvent> incoming, IReadOnlyList<string> incomingKeys, IReadOnlySet<string> scope)
    {
        List<EventCorrection> corrections;
        List<GameEvent> marked;
        try
        {
            corrections = await EventCorrectionSql.LoadApplicableAsync(conn, tx, gameId);
            marked = await EventCorrectionSql.LoadMarkedRowsAsync(conn, tx, gameId);
        }
        catch (SqliteException ex)
        {
            // No ledger on this database (or an older schema): behave exactly as before v16.
            CoreDiagnostics.WriteVerbose($"Event corrections: planner fell back to pass-through for game {gameId}: {ex.Message}");
            return ReconcilePlan.PassThrough(incoming, incomingKeys);
        }

        var plan = new PlanState(gameId, incoming, incomingKeys, scope, marked);
        foreach (var c in corrections)
        {
            try
            {
                await PlanOneAsync(conn, tx, plan, c);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CoreDiagnostics.WriteVerbose($"Event corrections: correction {c.CorrectionId} failed to plan: {ex.Message}");
                await RecordErrorAsync(conn, tx, c, ex);
            }
        }
        return plan.Build();
    }

    private static async Task PlanOneAsync(SqliteConnection conn, SqliteTransaction tx, PlanState plan, EventCorrection c)
    {
        var survivor = plan.SurvivorOf(c);

        // The corrected row itself is in the batch (a GetEventsAsync round-trip). It is already
        // suppressed by the mask; when the table still holds it nothing changes, when the table
        // lost it (DeleteEventsAsync) the batch copy is the re-insert.
        if (plan.SelfIndexOf(c) is { } selfIndex)
        {
            if (survivor is null)
            {
                var copy = Clone(plan.Incoming[selfIndex]);
                copy.EventKey = c.SubjectKey;
                plan.InsertPatched(selfIndex, copy, c.SubjectKey, c.Id, c.State);
            }
            return;
        }

        var match = EventIdentity.FindMatch(c, plan.Incoming, plan.IncomingKeys, plan.Claimed);
        var inScope = plan.InScope(c.SubjectType) || plan.InScope(c.EffectiveType);

        switch (c.Op)
        {
            case CorrectionOps.Add:
                if (match is not null)
                {
                    if (survivor is not null)
                    {
                        plan.Suppress(match.Index);
                        await SetStateAsync(conn, tx, c, CorrectionStates.Absorbed, c.AppliedEventId);
                    }
                    else
                    {
                        // The user's row is gone and the detector now produces the event: the twin
                        // becomes the user's row rather than vanishing with it.
                        var clone = Clone(plan.Incoming[match.Index]);
                        EventPatching.Apply(clone, c.Patch, EventOriginal.None, c.CorrectionId, c.Op, c.SubjectKey, c.Reason);
                        plan.InsertPatched(match.Index, clone, c.SubjectKey, c.Id, CorrectionStates.Absorbed);
                    }
                    plan.Absorbed++;
                }
                else if (survivor is null && plan.InScope(c.EffectiveType))
                {
                    var e = new GameEvent { GameId = plan.GameId, EventType = c.EffectiveType, GameTimeS = c.EffectiveTimeS, Details = "{}" };
                    EventPatching.Apply(e, c.Patch, EventOriginal.None, c.CorrectionId, c.Op, c.SubjectKey, c.Reason);
                    plan.Extras.Add(new ExtraInsert(e, c.SubjectKey, c.Id, CorrectionStates.Active));
                }
                break;

            case CorrectionOps.Remove:
                if (match is not null)
                {
                    plan.Suppress(match.Index);
                    await SetStateAsync(conn, tx, c, CorrectionStates.Active, null);
                }
                else if (inScope)
                {
                    await SetStateAsync(conn, tx, c, CorrectionStates.Absorbed, null);
                    plan.Absorbed++;
                }
                break;

            default:
                if (match is not null)
                {
                    var twin = plan.Incoming[match.Index];
                    var key = match.CandidateKey;
                    var fuzzy = !match.Exact && !string.Equals(key, c.SubjectKey, StringComparison.Ordinal);
                    var state = EventIdentity.Satisfies(twin, c.Patch, c.Original) ? CorrectionStates.Absorbed : CorrectionStates.Active;
                    if (fuzzy) await EventCorrectionSql.RebaseSubjectAsync(conn, tx, c.Id, key, c.SubjectKey);
                    if (survivor is not null)
                    {
                        plan.Suppress(match.Index);
                        if (fuzzy) await EventCorrectionSql.SetEventKeyAsync(conn, tx, survivor.Id, key);
                        await SetStateAsync(conn, tx, c, state, c.AppliedEventId ?? survivor.Id);
                    }
                    else
                    {
                        var clone = Clone(twin);
                        EventPatching.Apply(clone, c.Patch, c.Original, c.CorrectionId, c.Op, key, c.Reason);
                        plan.InsertPatched(match.Index, clone, key, c.Id, state);
                    }
                    if (state == CorrectionStates.Absorbed) plan.Absorbed++;
                }
                else if (inScope)
                {
                    await SetStateAsync(conn, tx, c, CorrectionStates.Orphaned, c.AppliedEventId);
                    plan.Orphaned++;
                }
                break;
        }
    }

    /// <summary>Mutable planning state for one batch; <see cref="Build"/> freezes it.</summary>
    private sealed class PlanState
    {
        private readonly IncomingAction[] _actions;
        private readonly GameEvent[] _rows;
        private readonly string[] _keys;
        private readonly long?[] _correctionIds;
        private readonly string?[] _states;
        private readonly Dictionary<string, int> _selfIndexes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GameEvent> _markedByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<long, GameEvent> _markedById = new();
        private readonly IReadOnlySet<string> _scope;

        public PlanState(long gameId, IReadOnlyList<GameEvent> incoming, IReadOnlyList<string> incomingKeys,
            IReadOnlySet<string> scope, List<GameEvent> marked)
        {
            GameId = gameId;
            Incoming = incoming;
            IncomingKeys = incomingKeys;
            _scope = scope;
            _actions = new IncomingAction[incoming.Count];
            _rows = new GameEvent[incoming.Count];
            _keys = new string[incoming.Count];
            _correctionIds = new long?[incoming.Count];
            _states = new string?[incoming.Count];
            for (var i = 0; i < incoming.Count; i++)
            {
                _rows[i] = incoming[i];
                _keys[i] = ReconcilePlan.KeyAt(incomingKeys, incoming, i);
                var details = incoming[i].Details ?? "";
                if (!EventPatching.HasMarker(details) && ReviewedEncountersRepository.ReviewDetails(details) is null) continue;
                // Already in the table (a corrected or reviewed row sent back to us): never a candidate.
                _actions[i] = IncomingAction.Suppress;
                Claimed.Add(i);
                if (EventPatching.ReadMarker(details) is { } m && m.Id.Length > 0) _selfIndexes.TryAdd(m.Id, i);
            }
            foreach (var row in marked)
            {
                if (row.EventKey is not null) _markedByKey.TryAdd(row.EventKey, row);
                _markedById.TryAdd(row.Id, row);
            }
        }

        public long GameId { get; }
        public IReadOnlyList<GameEvent> Incoming { get; }
        public IReadOnlyList<string> IncomingKeys { get; }
        public HashSet<int> Claimed { get; } = [];
        public List<ExtraInsert> Extras { get; } = [];
        public int Absorbed { get; set; }
        public int Orphaned { get; set; }

        public bool InScope(string? type) => type is not null && _scope.Contains(type.ToUpperInvariant());

        public GameEvent? SurvivorOf(EventCorrection c)
        {
            if (_markedByKey.TryGetValue(c.SubjectKey, out var byKey)) return byKey;
            if (c.AppliedEventId is { } id && _markedById.TryGetValue(id, out var byId)) return byId;
            return null;
        }

        public int? SelfIndexOf(EventCorrection c) => _selfIndexes.TryGetValue(c.CorrectionId, out var i) ? i : null;

        public void Suppress(int index)
        {
            _actions[index] = IncomingAction.Suppress;
            _correctionIds[index] = null;
            Claimed.Add(index);
        }

        public void InsertPatched(int index, GameEvent row, string key, long correctionId, string state)
        {
            _actions[index] = IncomingAction.InsertPatched;
            _rows[index] = row;
            _keys[index] = key;
            _correctionIds[index] = correctionId;
            _states[index] = state;
            Claimed.Add(index);
        }

        public ReconcilePlan Build()
        {
            var decisions = new IncomingDecision[Incoming.Count];
            var suppressed = 0;
            var reapplied = 0;
            for (var i = 0; i < Incoming.Count; i++)
            {
                if (_actions[i] == IncomingAction.Suppress) suppressed++;
                if (_actions[i] == IncomingAction.InsertPatched) reapplied++;
                decisions[i] = new IncomingDecision(i, _actions[i], _rows[i], _keys[i], _correctionIds[i], _states[i]);
            }
            return new ReconcilePlan(decisions, Extras, suppressed, Absorbed, Orphaned, reapplied);
        }
    }
}

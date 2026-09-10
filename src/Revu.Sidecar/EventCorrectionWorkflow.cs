#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// The sidecar's write seam for the event corrections ledger (v3.11): every fix and every
/// revert goes through <see cref="IEventCorrectionsRepository"/> (rules D and E, one
/// transaction inside Revu.Core) and, when the ledger actually changed, rule F runs here:
/// the derived-event instances are recomputed and the game's pattern evidence is
/// re-materialized so a retimed or removed event never leaves a stale region or anchor
/// behind. Rule F is best-effort: it is logged and never fails the write that triggered it.
/// </summary>
public sealed class EventCorrectionWorkflow
{
    private readonly IEventCorrectionsRepository _corrections;
    private readonly IGameEventsRepository _events;
    private readonly IDerivedEventsRepository _derived;
    private readonly IPatternEvidenceMaterializer _materializer;
    private readonly ILogger<EventCorrectionWorkflow> _logger;

    public EventCorrectionWorkflow(
        IEventCorrectionsRepository corrections,
        IGameEventsRepository events,
        IDerivedEventsRepository derived,
        IPatternEvidenceMaterializer materializer,
        ILogger<EventCorrectionWorkflow> logger)
    {
        _corrections = corrections;
        _events = events;
        _derived = derived;
        _materializer = materializer;
        _logger = logger;
    }

    /// <summary>Rule D then rule F. An idempotent replay (same correction id) changes nothing
    /// and therefore refreshes nothing.</summary>
    public async Task<EventCorrectionSaveResult> SaveAsync(EventCorrectionRequest request)
    {
        var r = await _corrections.SaveAsync(request);
        if (!r.Idempotent) await RefreshDerivedAsync(request.GameId);
        return r;
    }

    /// <summary>Rule E then rule F. Reverting an already reverted correction is idempotent.</summary>
    public async Task<EventCorrectionSaveResult> RevertAsync(long gameId, string correctionId, string reason = "", string appVersion = "")
    {
        var r = await _corrections.RevertAsync(gameId, correctionId, reason, appVersion);
        if (!r.Idempotent) await RefreshDerivedAsync(gameId);
        return r;
    }

    /// <summary>Rule F: recompute derived_event_instances (SaveInstancesAsync ALWAYS, so a zero
    /// result clears stale rows) and re-materialize pattern evidence. Best-effort, logged,
    /// never throws. map_state_v is untouched: the post-game pass is not re-queued by a fix.</summary>
    public async Task RefreshDerivedAsync(long gameId)
    {
        try
        {
            var events = await _events.GetEventsAsync(gameId);
            var definitions = await _derived.GetAllDefinitionsAsync();
            var instances = _derived.ComputeInstances(gameId, events, definitions);
            await _derived.SaveInstancesAsync(gameId, instances);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event corrections: derived-event recompute failed for game {GameId}", gameId);
        }

        try
        {
            await _materializer.MaterializeForGameAsync(gameId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event corrections: pattern evidence refresh failed for game {GameId}", gameId);
        }
    }
}

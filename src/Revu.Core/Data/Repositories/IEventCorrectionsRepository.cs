#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>
/// The event corrections ledger (schema v16): append-only user fixes on timeline events, applied
/// in place to game_events and keyed on the row's stable <c>event_key</c> so they survive
/// re-capture. Every validation failure is an <see cref="ArgumentException"/> whose message is
/// the exact sentence the UI shows.
/// </summary>
public interface IEventCorrectionsRepository
{
    /// <summary>Rule D. Validates (ArgumentException with the user-facing sentences of section 4),
    /// idempotent on CorrectionId (any state returns the stored row, Idempotent = true, no write),
    /// supersedes the prior applicable row on the same subject, applies in place, one transaction.</summary>
    Task<EventCorrectionSaveResult> SaveAsync(EventCorrectionRequest request);

    /// <summary>Rule E. Restores the subject from Original, flips the target to reverted, writes an op revert row.
    /// Idempotent when the target is already reverted.</summary>
    Task<EventCorrectionSaveResult> RevertAsync(long gameId, string correctionId, string reason = "", string appVersion = "");

    /// <summary>Every non-revert row of the game, newest first (all states).</summary>
    Task<IReadOnlyList<EventCorrection>> GetForGameAsync(long gameId);

    /// <summary>Applicable rows (active, absorbed, orphaned; op &lt;&gt; revert), id ASC.</summary>
    Task<IReadOnlyList<EventCorrection>> GetActiveForGameAsync(long gameId);

    Task<int> CountActiveForGameAsync(long gameId);

    /// <summary>Phase 1 local export (unredacted, see section 8). gameId null = every game.</summary>
    Task<string> ExportAsync(long? gameId, string appVersion);

    /// <summary>The rows <see cref="ExportAsync"/> serialises (revert rows included), oldest first,
    /// so a caller can count them without re-parsing the JSON.</summary>
    Task<IReadOnlyList<EventCorrection>> ExportItemsAsync(long? gameId);

    /// <summary>Rule G, first half: stamp event_key where NULL, per game, at most <paramref name="limit"/> rows.
    /// Returns rows stamped.</summary>
    Task<int> StampMissingEventKeysAsync(int limit);
}

#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// The games a manual (Settings) backfill leg already tried this session. A game that stays
/// queued after its lookup can never resolve, or not yet: a manual entry, a remake with no
/// laning numbers, a match past Match-V5 retention, a lookup that failed. The queues are
/// walked newest-first, so without this record such games at the head were re-fetched on
/// every click and a time-capped run never got past them. A leg given one walks untried
/// games first and the tried ones after (a transient failure is still retried when time is
/// left), and counts only untried games as remaining, so "run it again" always reaches new
/// games. In memory only: a restart tries every game once more.
/// </summary>
public sealed class BackfillAttempts
{
    private readonly object _gate = new();
    private readonly HashSet<long> _tried = new();

    /// <summary><paramref name="queue"/> with the untried games first, each part in its original order.</summary>
    public List<long> Order(IReadOnlyList<long> queue)
    {
        lock (_gate) return queue.OrderBy(id => _tried.Contains(id)).ToList();
    }

    public void Tried(long gameId)
    {
        lock (_gate) _tried.Add(gameId);
    }

    /// <summary>How many of <paramref name="unreached"/> no run has tried yet this session.</summary>
    public int CountUntried(IEnumerable<long> unreached)
    {
        lock (_gate) return unreached.Count(id => !_tried.Contains(id));
    }
}

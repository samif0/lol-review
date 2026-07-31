#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// Repository for coaching stints (v3.3, schema v12) — months-long
/// engagements with a named coach that group and count session blocks.
/// At most one stint is active (ended_at NULL) at a time; callers gate
/// StartStintAsync on GetActiveStintAsync returning null.
/// </summary>
public interface ICoachingStintsRepository
{
    /// <summary>
    /// Create a stint and return its id. Does not enforce single-active
    /// itself — the write route checks GetActiveStintAsync first so the
    /// user gets a clear "end the current stint" error instead of a
    /// constraint failure.
    /// </summary>
    /// <param name="name">Display name, e.g. the coach's name.</param>
    /// <param name="startDate">First day, "yyyy-MM-dd".</param>
    /// <param name="plannedEndDate">Optional target end date, "" = open-ended.</param>
    Task<int> StartStintAsync(string name, string startDate, string plannedEndDate = "");

    /// <summary>Close a stint (stamp ended_at). No-op if already ended.</summary>
    Task EndStintAsync(int stintId);

    /// <summary>The single active stint (ended_at NULL), or null. When
    /// multiple rows are somehow active, the newest wins.</summary>
    Task<CoachingStint?> GetActiveStintAsync();

    /// <summary>
    /// The next 1-based block number for a stint: highest stamped
    /// stint_block_number + 1. Re-locking today's block keeps its original
    /// number (the sessions upsert is sticky), so the sequence never skips
    /// or double-counts.
    /// </summary>
    Task<int> GetNextBlockNumberAsync(int stintId);

    /// <summary>Blocks recorded against a stint, split by the with_coach
    /// tag — the data the stint exists to gather.</summary>
    Task<CoachingStintBlockCounts> GetBlockCountsAsync(int stintId);
}

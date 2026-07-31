#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A coaching stint — a months-long engagement with a named coach
/// (v3.3, schema v12). Maps to the coaching_stints table. At most one
/// stint is active (ended_at NULL) at a time; blocks started while a
/// stint is active are stamped with its id and a 1-based block number.
/// </summary>
public class CoachingStint
{
    public int Id { get; set; }

    /// <summary>Display name, e.g. the coach's name ("Violet").</summary>
    public string Name { get; set; } = "";

    /// <summary>First day of the stint ("yyyy-MM-dd").</summary>
    public string StartDate { get; set; } = "";

    /// <summary>Optional target end date ("yyyy-MM-dd", "" = open-ended).</summary>
    public string PlannedEndDate { get; set; } = "";

    public long? CreatedAt { get; set; }

    /// <summary>Unix seconds when the stint was closed; NULL = active.</summary>
    public long? EndedAt { get; set; }
}

/// <summary>
/// Per-tag block counts for a stint — how many blocks ran with the coach
/// present vs solo. Total is the stamped block count, so it equals the
/// highest stint_block_number handed out.
/// </summary>
public sealed record CoachingStintBlockCounts(
    int Total,
    int WithCoach,
    int Solo
);

#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// Session-level intentions and debriefs (Gollwitzer 1999).
/// Maps to the sessions table — one row per play-session date.
/// </summary>
public class SessionInfo
{
    public int Id { get; set; }

    /// <summary>Unique date string for the session (e.g. "2024-03-15").</summary>
    public string Date { get; set; } = "";

    public string Intention { get; set; } = "";
    public int DebriefRating { get; set; }
    public string DebriefNote { get; set; } = "";
    public long? StartedAt { get; set; }
    public long? EndedAt { get; set; }

    /// <summary>v3.3 (schema v12): coaching stint this block belongs to,
    /// or null for a standalone block.</summary>
    public int? StintId { get; set; }

    /// <summary>1-based block number within the stint; stamped at Start
    /// Block and never renumbered. Null when not part of a stint.</summary>
    public int? StintBlockNumber { get; set; }

    /// <summary>The block ran with the coach present — its games are
    /// reviewed with the coach outside Revu and skip the review queue.</summary>
    public bool WithCoach { get; set; }
}

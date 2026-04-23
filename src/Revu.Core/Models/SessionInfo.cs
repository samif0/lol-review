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
}

#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A single entry in the session log — one row per game played during a session.
/// </summary>
public class SessionLogEntry
{
    public int Id { get; set; }
    public string Date { get; set; } = "";
    public long? GameId { get; set; }
    public string ChampionName { get; set; } = "";
    public bool Win { get; set; }
    public int MentalRating { get; set; } = 5;
    public string ImprovementNote { get; set; } = "";
    public int RuleBroken { get; set; }
    public long Timestamp { get; set; }
    public string PregameIntention { get; set; } = "";

    /// <summary>
    /// v2.18 (schema v6): where the pre-game intention came from —
    /// "" (legacy/none), "carry" (zero-tap default from last review's
    /// focus_next), "objective" (seeded from an objective chip), or
    /// "edited" (user typed). Keeps zero-tap carries distinguishable from
    /// deliberate intent for the intention-echo analysis.
    /// </summary>
    public string IntentionSource { get; set; } = "";

    public string MentalHandled { get; set; } = "";
    public int PreGameMood { get; set; }

    /// <summary>
    /// v2.18 (schema v5): one-tap "did you do your focus?" answer.
    /// null = unanswered, 0 = no, 1 = partly, 2 = yes.
    /// </summary>
    public int? FocusAdherence { get; set; }
}

#nullable enable

namespace LoLReview.Core.Models;

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
    public string MentalHandled { get; set; } = "";
    public int PreGameMood { get; set; }
}

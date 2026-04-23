#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A player's note about a specific champion matchup.
/// </summary>
public class MatchupNote
{
    public int Id { get; set; }
    public string Champion { get; set; } = "";
    public string Enemy { get; set; } = "";
    public string Note { get; set; } = "";
    public bool? Helpful { get; set; }
    public int? GameId { get; set; }
    public long? CreatedAt { get; set; }
}

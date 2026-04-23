#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A VOD recording file linked to a specific game.
/// </summary>
public class VodFile
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public int DurationS { get; set; }
    public long? MatchedAt { get; set; }
}

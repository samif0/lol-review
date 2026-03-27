#nullable enable

namespace LoLReview.Core.Models;

/// <summary>
/// A bookmark on a VOD recording at a specific game time, with optional clip region.
/// </summary>
public class VodBookmark
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int GameTimeS { get; set; }
    public string Note { get; set; } = "";
    public string Tags { get; set; } = "[]";
    public int? ClipStartS { get; set; }
    public int? ClipEndS { get; set; }
    public string ClipPath { get; set; } = "";
    public long? CreatedAt { get; set; }
}

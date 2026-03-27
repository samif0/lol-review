#nullable enable

namespace LoLReview.Core.Models;

/// <summary>
/// A learning objective the player is working on.
/// </summary>
public class Objective
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string SkillArea { get; set; } = "";
    public string Type { get; set; } = "primary";
    public string CompletionCriteria { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "active";
    public int Score { get; set; }
    public int GameCount { get; set; }
    public long? CreatedAt { get; set; }
    public long? CompletedAt { get; set; }
}

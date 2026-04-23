#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A personal rule the player wants to follow (e.g. "Stop playing after 3 losses").
/// </summary>
public class Rule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string RuleType { get; set; } = "custom";
    public string ConditionValue { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public long? CreatedAt { get; set; }
}

#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// Definition for a derived event (e.g. Teamfight, Skirmish) — computed
/// from clusters of raw events within a time window.
/// </summary>
public class DerivedEventDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> SourceTypes { get; set; } = [];
    public int MinCount { get; set; }
    public int WindowSeconds { get; set; }
    public string Color { get; set; } = "#ff6b6b";
    public bool IsDefault { get; set; }
    public long? CreatedAt { get; set; }
}

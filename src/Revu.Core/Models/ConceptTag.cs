#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A reusable concept tag that can be applied to games (e.g. "Caught out", "Dominated lane").
/// </summary>
public class ConceptTag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Polarity { get; set; } = "neutral";
    public string Color { get; set; } = "#3b82f6";
}

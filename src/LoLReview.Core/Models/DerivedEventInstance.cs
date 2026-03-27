#nullable enable

namespace LoLReview.Core.Models;

/// <summary>
/// An actual instance of a derived event detected in a specific game.
/// </summary>
public class DerivedEventInstance
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int DefinitionId { get; set; }
    public int StartTimeS { get; set; }
    public int EndTimeS { get; set; }
    public int EventCount { get; set; }
    public List<int> SourceEventIds { get; set; } = [];

    /// <summary>Denormalized from the definition for display convenience.</summary>
    public string DefinitionName { get; set; } = "";

    /// <summary>Denormalized from the definition for display convenience.</summary>
    public string Color { get; set; } = "";
}

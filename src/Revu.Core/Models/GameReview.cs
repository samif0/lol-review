#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// Post-game review data collected from the review UI.
/// </summary>
public class GameReview
{
    public int Rating { get; set; }
    public string Notes { get; set; } = "";
    public string Tags { get; set; } = "[]";
    public string Mistakes { get; set; } = "";
    public string WentWell { get; set; } = "";
    public string FocusNext { get; set; } = "";
    public string SpottedProblems { get; set; } = "";
    public string OutsideControl { get; set; } = "";
    public string WithinControl { get; set; } = "";
    public string Attribution { get; set; } = "";
    public string PersonalContribution { get; set; } = "";
}

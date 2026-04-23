#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// Unsaved review state persisted while the user pivots into VOD review.
/// This keeps partial notes from being lost without marking the game as fully reviewed.
/// </summary>
public sealed class ReviewDraft
{
    public long GameId { get; set; }
    public int MentalRating { get; set; } = 5;
    public string WentWell { get; set; } = "";
    public string Mistakes { get; set; } = "";
    public string FocusNext { get; set; } = "";
    public string ReviewNotes { get; set; } = "";
    public string ImprovementNote { get; set; } = "";
    public string Attribution { get; set; } = "";
    public string MentalHandled { get; set; } = "";
    public string SpottedProblems { get; set; } = "";
    public string OutsideControl { get; set; } = "";
    public string WithinControl { get; set; } = "";
    public string PersonalContribution { get; set; } = "";
    public string EnemyLaner { get; set; } = "";
    public string MatchupNote { get; set; } = "";
    public string SelectedTagIdsJson { get; set; } = "[]";
    public string ObjectiveAssessmentsJson { get; set; } = "[]";
    public long UpdatedAt { get; set; }
}

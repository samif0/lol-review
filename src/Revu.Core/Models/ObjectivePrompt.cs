#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A prompt question attached to an objective, shown during review.
/// </summary>
public class ObjectivePrompt
{
    public int Id { get; set; }
    public int ObjectiveId { get; set; }
    public string QuestionText { get; set; } = "";
    public string EventTag { get; set; } = "";
    public string AnswerType { get; set; } = "yes_no";
    public int SortOrder { get; set; }
}

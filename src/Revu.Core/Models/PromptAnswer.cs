#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// An answer to an objective prompt for a specific game/event.
/// </summary>
public class PromptAnswer
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int PromptId { get; set; }
    public int? EventInstanceId { get; set; }
    public int? EventTimeS { get; set; }
    public int AnswerValue { get; set; }
}

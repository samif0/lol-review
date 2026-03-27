#nullable enable

namespace LoLReview.Core.Models;

/// <summary>
/// A persistent note stored in the database — general-purpose scratchpad.
/// </summary>
public class PersistentNote
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public long? UpdatedAt { get; set; }
}

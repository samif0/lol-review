#nullable enable

namespace Revu.Core.Models;

public sealed class VodPlayerNavigationRequest
{
    public long GameId { get; set; }
    public int? SeekTimeS { get; set; }
}

#nullable enable

namespace Revu.Core.Models;

public sealed class VodPlayerNavigationRequest
{
    public long GameId { get; set; }
    public int? SeekTimeS { get; set; }
    /// <summary>v2.15.9: when true, the page enters fullscreen on load. Set
    /// only by the post-game landing path so user-initiated VOD navigation
    /// (Dashboard "Watch VOD", review "Review VOD") doesn't take over the
    /// window.</summary>
    public bool AutoFullscreen { get; set; }
}

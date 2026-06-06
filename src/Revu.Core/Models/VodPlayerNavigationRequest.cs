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

    /// <summary>
    /// v2.18 (F1): when set, the VOD viewer opens "focused" on this one objective
    /// — its tag/objective picker shows ONLY this objective and its prompts,
    /// hiding all others. Set when opening a VOD from a pattern card or from an
    /// objective's games list, so the review is scoped to that objective. Null
    /// (the default, and all bare-long navigations) shows every active objective.
    /// </summary>
    public long? FocusObjectiveId { get; set; }

    /// <summary>
    /// Optional dashboard pattern kind that should constrain the auto-moment
    /// list on open, e.g. isolated deaths or deaths before objectives.
    /// </summary>
    public string AutoMomentPatternKind { get; set; } = "";
}

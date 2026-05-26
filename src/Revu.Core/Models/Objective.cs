#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// A learning objective the player is working on.
/// </summary>
public class Objective
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string SkillArea { get; set; } = "";

    /// <summary>
    /// "primary" (the default, open-ended) or "mini" (a short-horizon focus
    /// item bounded by <see cref="TargetGameCount"/>, e.g. "flash usage, next
    /// 3 games"). Mini objectives auto-archive once
    /// <see cref="GameCount"/> ≥ <see cref="TargetGameCount"/>.
    /// </summary>
    public string Type { get; set; } = "primary";

    public string CompletionCriteria { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "active";
    public int Score { get; set; }
    public int GameCount { get; set; }

    /// <summary>
    /// v2.17.7: only meaningful for mini objectives. Number of games this
    /// objective is scoped to. Zero means "no target" (primary objectives).
    /// </summary>
    public int TargetGameCount { get; set; }

    public long? CreatedAt { get; set; }
    public long? CompletedAt { get; set; }

    public bool IsMini => string.Equals(Type, "mini", StringComparison.OrdinalIgnoreCase);
    public int GamesRemaining => Math.Max(0, TargetGameCount - GameCount);
    public bool IsMiniComplete => IsMini && TargetGameCount > 0 && GameCount >= TargetGameCount;
}

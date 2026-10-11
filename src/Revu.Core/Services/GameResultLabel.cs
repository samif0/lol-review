#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// 3.14.1: the one result label every surface shows. A remake is always the word
/// "Remake" (never W or L): it is saved and reviewable but does not count.
/// </summary>
public static class GameResultLabel
{
    public const string Remake = "Remake";

    /// <summary>"Remake" | "Win" | "Loss".</summary>
    public static string For(GameStats game) => For(game.Win, game.IsRemake);

    /// <summary>"Remake" | "Win" | "Loss".</summary>
    public static string For(bool win, bool isRemake) => isRemake ? Remake : win ? "Win" : "Loss";

    /// <summary>"Remake" | "W" | "L" for the compact row badges.</summary>
    public static string Short(bool win, bool isRemake) => isRemake ? Remake : win ? "W" : "L";
}

#nullable enable

namespace Revu.Core.Data.Repositories;

public static class ObjectivePhases
{
    public const string PreGame = "pregame";
    public const string InGame = "ingame";
    public const string PostGame = "postgame";

    public static string Normalize(string? value)
    {
        var compact = (value ?? "")
            .Trim()
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant();

        return compact switch
        {
            "pregame" => PreGame,
            "postgame" => PostGame,
            "ingame" => InGame,
            _ => InGame,
        };
    }

    public static string ToDisplayLabel(string? value) => Normalize(value) switch
    {
        PreGame => "Pre-Game",
        PostGame => "Post-Game",
        _ => "In-Game",
    };

    public static int ToIndex(string? value) => Normalize(value) switch
    {
        PreGame => 0,
        InGame => 1,
        PostGame => 2,
        _ => 1,
    };

    public static string FromIndex(int index) => index switch
    {
        0 => PreGame,
        2 => PostGame,
        _ => InGame,
    };

    public static bool ShowsInPreGame(string? value)
    {
        var phase = Normalize(value);
        return phase == PreGame;
    }

    public static bool ShowsInPostGame(string? value)
    {
        var phase = Normalize(value);
        return phase == PreGame || phase == InGame || phase == PostGame;
    }
}

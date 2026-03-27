#nullable enable

namespace LoLReview.Core.Lcu;

/// <summary>
/// Represents the League client gameflow phase.
/// </summary>
public enum GamePhase
{
    None,
    Lobby,
    ReadyCheck,
    ChampSelect,
    GameStart,
    InProgress,
    WaitingForStats,
    EndOfGame,
    PreEndOfGame,
    Reconnect,
}

/// <summary>
/// Helper methods for <see cref="GamePhase"/>.
/// </summary>
public static class GamePhaseExtensions
{
    /// <summary>
    /// Parse a gameflow phase string from the LCU API into a <see cref="GamePhase"/> enum value.
    /// </summary>
    public static GamePhase ParsePhase(string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
            return GamePhase.None;

        return phase.Trim() switch
        {
            "None" => GamePhase.None,
            "Lobby" => GamePhase.Lobby,
            "ReadyCheck" => GamePhase.ReadyCheck,
            "ChampSelect" => GamePhase.ChampSelect,
            "GameStart" => GamePhase.GameStart,
            "InProgress" => GamePhase.InProgress,
            "WaitingForStats" => GamePhase.WaitingForStats,
            "EndOfGame" => GamePhase.EndOfGame,
            "PreEndOfGame" => GamePhase.PreEndOfGame,
            "Reconnect" => GamePhase.Reconnect,
            _ => GamePhase.None,
        };
    }
}

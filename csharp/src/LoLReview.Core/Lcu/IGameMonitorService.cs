#nullable enable

namespace LoLReview.Core.Lcu;

/// <summary>
/// Monitors the League client for game phase transitions and fires messages
/// when champ select starts, game starts, and game ends.
/// </summary>
public interface IGameMonitorService
{
    /// <summary>
    /// Callback to check whether a game with the given ID has already been saved.
    /// Used during match history reconciliation to avoid duplicates.
    /// </summary>
    Func<int, bool>? CheckGameSaved { get; set; }
}

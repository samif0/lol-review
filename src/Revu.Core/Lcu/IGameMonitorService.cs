#nullable enable

namespace Revu.Core.Lcu;

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
    Func<long, bool>? CheckGameSaved { get; set; }

    /// <summary>
    /// Current LCU connection state. Lets late-constructed consumers (e.g. a
    /// ShellViewModel created after onboarding) query the live state instead of
    /// relying on the edge-triggered <c>LcuConnectionChangedMessage</c> they
    /// may have missed.
    /// </summary>
    bool IsConnected { get; }
}

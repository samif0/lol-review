#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Lcu;

/// <summary>
/// Discovers LCU credentials from the running League client process or lockfile.
/// </summary>
public interface ILcuCredentialDiscovery
{
    /// <summary>
    /// Find LCU credentials using all available methods (process inspection, then lockfile).
    /// </summary>
    LcuCredentials? FindCredentials();

    /// <summary>
    /// Find LCU credentials by inspecting the LeagueClientUx process command line via WMI.
    /// </summary>
    LcuCredentials? FindFromProcess();

    /// <summary>
    /// Find LCU credentials by reading the lockfile from common install locations.
    /// </summary>
    LcuCredentials? FindFromLockfile();
}

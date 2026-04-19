#nullable enable

namespace LoLReview.App.Services;

/// <summary>
/// Tracks and manages the optional coach-ml extras pack (torch +
/// sentence-transformers + hdbscan). Installing it enables the
/// /concepts/* endpoints — without it, those endpoints return HTTP 501
/// with error code `ml_extras_not_installed`.
///
/// Version-pinned to the running app version, same as the core pack.
/// </summary>
public interface ICoachMlExtrasInstallerService
{
    /// <summary>True if the ML extras pack is installed and the
    /// site-packages dir exists where coach/_extras.py probes for it.</summary>
    bool IsInstalled { get; }

    /// <summary>Install (download + verify + extract) the ML extras pack.</summary>
    Task<CoachInstallResult> InstallAsync(
        IProgress<CoachInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Remove the installed ML extras pack.</summary>
    Task UninstallAsync(CancellationToken cancellationToken = default);
}

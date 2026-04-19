#nullable enable

namespace LoLReview.App.Services;

/// <summary>
/// Tracks and manages the opt-in coach sidecar installation.
///
/// Per the plan amendment (2026-04-18), the base installer does NOT bundle
/// the Python sidecar. Enabling coaching triggers a download into
/// %LOCALAPPDATA%\LoLReviewData\coach\bin\.
/// </summary>
public interface ICoachInstallerService
{
    /// <summary>True if the sidecar is installed and executable is present.</summary>
    bool IsInstalled { get; }

    /// <summary>Absolute path to the sidecar executable, or null if not installed.</summary>
    string? SidecarExecutablePath { get; }

    /// <summary>
    /// Version of the currently installed pack (from manifest.json), or
    /// null if not installed / manifest unreadable.
    /// </summary>
    string? InstalledVersion { get; }

    /// <summary>Bytes on disk for the installed pack, or 0 if not installed.</summary>
    long InstalledSizeBytes { get; }

    /// <summary>Install (download + verify) the sidecar. Progress reported via <paramref name="progress"/>.</summary>
    Task<CoachInstallResult> InstallAsync(IProgress<CoachInstallProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Remove the installed sidecar. Does not touch user data or DB.</summary>
    Task UninstallAsync(CancellationToken cancellationToken = default);
}

public enum CoachInstallStatus
{
    NotInstalled,
    Downloading,
    Verifying,
    Ready,
    Error,
}

public record CoachInstallProgress(CoachInstallStatus Status, double PercentComplete, string? Message);

public record CoachInstallResult(bool Success, string? SidecarPath, string? Error);

#nullable enable

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Revu.App.Services;

public interface IUpdateService
{
    /// <summary>Check GitHub for a newer version. Returns null if up to date or not installed via Velopack.</summary>
    Task<UpdateInfo?> CheckForUpdateAsync(bool force = false);

    /// <summary>Download the update package. Calls onProgress(0..100) during download.</summary>
    Task DownloadUpdateAsync(UpdateInfo update, Action<int>? onProgress = null);

    /// <summary>Apply the downloaded update and restart the app.</summary>
    void ApplyUpdateAndRestart(UpdateInfo update);

    /// <summary>True when the app was installed via Velopack (not running from build output).</summary>
    bool IsInstalled { get; }

    /// <summary>Current version string (e.g. "2.0.0"). Returns "dev" when not installed.</summary>
    string CurrentVersion { get; }

    /// <summary>Most recently discovered update, if any.</summary>
    UpdateInfo? AvailableUpdate { get; }

    /// <summary>Whether an update is currently known to be available.</summary>
    bool IsUpdateAvailable { get; }

    /// <summary>Version string for the available update, if one was found.</summary>
    string AvailableVersion { get; }

    /// <summary>Whether at least one update check has completed.</summary>
    bool HasChecked { get; }

    /// <summary>Whether an update check is currently in progress.</summary>
    bool IsChecking { get; }

    /// <summary>Whether the last update check failed.</summary>
    bool LastCheckFailed { get; }

    /// <summary>Current human-readable status for the update system.</summary>
    string StatusText { get; }

    /// <summary>Raised whenever update status changes.</summary>
    event EventHandler? StateChanged;
}

public sealed class UpdateService : IUpdateService
{
    private const string GitHubRepoUrl = "https://github.com/samif0/lol-review";
    private const string ReleaseFeedUrl = "https://github.com/samif0/lol-review/releases/latest/download";
    private const string LatestReleasePageUrl = "https://github.com/samif0/lol-review/releases/latest";

    private readonly UpdateManager _mgr;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        var source = new SimpleWebSource(ReleaseFeedUrl);
        _mgr = new UpdateManager(source);
    }

    public event EventHandler? StateChanged;

    public bool IsInstalled => _mgr.IsInstalled;

    public string CurrentVersion => _mgr.IsInstalled
        ? _mgr.CurrentVersion?.ToString() ?? "unknown"
        : "dev";

    public UpdateInfo? AvailableUpdate { get; private set; }

    public bool IsUpdateAvailable => AvailableUpdate is not null;

    public string AvailableVersion => AvailableUpdate?.TargetFullRelease.Version.ToString() ?? "";

    public bool HasChecked { get; private set; }

    public bool IsChecking { get; private set; }

    public bool LastCheckFailed { get; private set; }

    public string StatusText { get; private set; } = "";

    public async Task<UpdateInfo?> CheckForUpdateAsync(bool force = false)
    {
        if (!_mgr.IsInstalled)
        {
            HasChecked = true;
            LastCheckFailed = false;
            AvailableUpdate = null;
            StatusText = "Update checks are available in the installed client.";
            OnStateChanged();
            return null;
        }

        if (!force && HasChecked && !IsChecking)
        {
            return AvailableUpdate;
        }

        await _checkLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!force && HasChecked && !IsChecking)
            {
                return AvailableUpdate;
            }

            IsChecking = true;
            LastCheckFailed = false;
            StatusText = "Checking for updates...";
            OnStateChanged();

            _logger.LogInformation("Checking for updates from {Url}, current version: {Version}",
                ReleaseFeedUrl, _mgr.CurrentVersion);

            var update = await _mgr.CheckForUpdatesAsync();
            AvailableUpdate = update;
            HasChecked = true;

            if (update != null)
            {
                _logger.LogInformation("Update available: {Version}", update.TargetFullRelease.Version);
                StatusText = $"Update available: v{update.TargetFullRelease.Version}";
            }
            else
            {
                _logger.LogInformation("App is up to date (v{Version})", _mgr.CurrentVersion);
                StatusText = "You're on the latest version";
            }

            return update;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            HasChecked = true;
            LastCheckFailed = true;
            AvailableUpdate = null;
            StatusText = "Update check failed";
            return null;
        }
        finally
        {
            IsChecking = false;
            OnStateChanged();
            _checkLock.Release();
        }
    }

    public async Task DownloadUpdateAsync(UpdateInfo update, Action<int>? onProgress = null)
    {
        try
        {
            _logger.LogInformation("Downloading update {Version}", update.TargetFullRelease.Version);
            await _mgr.DownloadUpdatesAsync(update, progress => onProgress?.Invoke(progress));
            _logger.LogInformation("Download complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update download failed for {Version}", update.TargetFullRelease.Version);
            TryOpenReleasePage();
            throw;
        }
    }

    public void ApplyUpdateAndRestart(UpdateInfo update)
    {
        _logger.LogInformation("Applying update and restarting");
        _mgr.ApplyUpdatesAndRestart(update);
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TryOpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LatestReleasePageUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open the latest release page after update download failure");
        }
    }
}

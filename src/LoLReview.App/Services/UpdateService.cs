#nullable enable

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace LoLReview.App.Services;

public interface IUpdateService
{
    /// <summary>Check GitHub for a newer version. Returns null if up to date or not installed via Velopack.</summary>
    Task<UpdateInfo?> CheckForUpdateAsync();

    /// <summary>Download the update package. Calls onProgress(0..100) during download.</summary>
    Task DownloadUpdateAsync(UpdateInfo update, Action<int>? onProgress = null);

    /// <summary>Apply the downloaded update and restart the app.</summary>
    void ApplyUpdateAndRestart(UpdateInfo update);

    /// <summary>True when the app was installed via Velopack (not running from build output).</summary>
    bool IsInstalled { get; }

    /// <summary>Current version string (e.g. "2.0.0"). Returns "dev" when not installed.</summary>
    string CurrentVersion { get; }
}

public sealed class UpdateService : IUpdateService
{
    private const string GitHubRepoUrl = "https://github.com/samif0/lol-review";
    private const string ReleaseFeedUrl = "https://github.com/samif0/lol-review/releases/latest/download";
    private const string LatestReleasePageUrl = "https://github.com/samif0/lol-review/releases/latest";

    private readonly UpdateManager _mgr;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        var source = new SimpleWebSource(ReleaseFeedUrl);
        _mgr = new UpdateManager(source);
    }

    public bool IsInstalled => _mgr.IsInstalled;

    public string CurrentVersion => _mgr.IsInstalled
        ? _mgr.CurrentVersion?.ToString() ?? "unknown"
        : "dev";

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            if (!_mgr.IsInstalled)
            {
                _logger.LogInformation("Not installed via Velopack, skipping update check");
                return null;
            }

            _logger.LogInformation("Checking for updates from {Url}, current version: {Version}",
                ReleaseFeedUrl, _mgr.CurrentVersion);

            var update = await _mgr.CheckForUpdatesAsync();
            if (update != null)
                _logger.LogInformation("Update available: {Version}", update.TargetFullRelease.Version);
            else
                _logger.LogInformation("App is up to date (v{Version})", _mgr.CurrentVersion);

            return update;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            return null;
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

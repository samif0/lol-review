#nullable enable

using Microsoft.Extensions.Logging;
using Velopack;

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

    private readonly UpdateManager _mgr;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        _mgr = new UpdateManager(GitHubRepoUrl);
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

            var update = await _mgr.CheckForUpdatesAsync();
            if (update != null)
                _logger.LogInformation("Update available: {Version}", update.TargetFullRelease.Version);
            else
                _logger.LogInformation("App is up to date");

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
        _logger.LogInformation("Downloading update {Version}", update.TargetFullRelease.Version);
        await _mgr.DownloadUpdatesAsync(update, progress => onProgress?.Invoke(progress));
        _logger.LogInformation("Download complete");
    }

    public void ApplyUpdateAndRestart(UpdateInfo update)
    {
        _logger.LogInformation("Applying update and restarting");
        _mgr.ApplyUpdatesAndRestart(update);
    }
}

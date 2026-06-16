#nullable enable

using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Revu.Sidecar;

/// <summary>
/// Update CHECK + DOWNLOAD for the desktop client. Ported from the old WinUI
/// Revu.App.Services.UpdateService, trimmed to the two operations Velopack's C#
/// UpdateManager owns cleanly from a child process: discovering a newer release on
/// the GitHub feed and staging its package locally.
///
/// The APPLY/restart step is NOT here: ApplyUpdatesAndRestart must run as the
/// installed main exe (revu-desktop.exe), which is the Rust/Tauri host, not this
/// sidecar. The host drives apply via the bundled Update.exe (see sidecar.rs /
/// lib.rs apply_update). This service only tells the UI "an update exists" and
/// "it's downloaded, here's the package path".
/// </summary>
public sealed class UpdateService
{
    // Same feed the release pipeline uploads to (vpk upload github → releases/latest).
    private const string ReleaseFeedUrl = "https://github.com/samif0/lol-review/releases/latest/download";

    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // UpdateManager is built lazily + defensively: constructing it (or touching
    // IsInstalled) can throw outside a real Velopack install, and a throwing DI
    // singleton 500s every update call. Lazy + try/catch degrades to "not
    // installed" instead.
    private UpdateManager? _mgr;
    private bool _mgrInitFailed;

    // Cache the last-discovered update so download/apply can reference the exact
    // UpdateInfo the check produced (Velopack wants the same object).
    private UpdateInfo? _available;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
    }

    private UpdateManager? Mgr()
    {
        if (_mgr is not null) return _mgr;
        if (_mgrInitFailed) return null;
        try
        {
            _mgr = new UpdateManager(new SimpleWebSource(ReleaseFeedUrl));
            return _mgr;
        }
        catch (Exception ex)
        {
            _mgrInitFailed = true;
            _logger.LogWarning(ex, "Velopack UpdateManager init failed (likely not a Velopack install)");
            return null;
        }
    }

    private static bool SafeIsInstalled(UpdateManager? mgr)
    {
        if (mgr is null) return false;
        try { return mgr.IsInstalled; }
        catch { return false; }
    }

    /// <summary>True when running from a real Velopack install (not a dev/build run).</summary>
    public bool IsInstalled => SafeIsInstalled(Mgr());

    public string CurrentVersion
    {
        get
        {
            var mgr = Mgr();
            if (!SafeIsInstalled(mgr)) return "dev";
            try { return mgr!.CurrentVersion?.ToString() ?? "unknown"; }
            catch { return "unknown"; }
        }
    }

    /// <summary>
    /// Check the GitHub feed for a newer release. Returns a small status shape the
    /// UI renders. Never throws — a failed check is reported, not fatal.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync()
    {
        var mgr = Mgr();
        if (!SafeIsInstalled(mgr))
        {
            // Dev / build-output run — no Velopack context. Report cleanly so the UI
            // can say "updates are managed in the installed app" instead of erroring.
            return new UpdateCheckResult(
                Ok: true, Installed: false, Available: false,
                CurrentVersion: CurrentVersion, NewVersion: null,
                Message: "Update checks run in the installed app.");
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Checking for updates from {Url} (current {Version})",
                ReleaseFeedUrl, mgr!.CurrentVersion);
            var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            _available = update;

            if (update is null)
            {
                _logger.LogInformation("Up to date (v{Version})", mgr.CurrentVersion);
                return new UpdateCheckResult(
                    Ok: true, Installed: true, Available: false,
                    CurrentVersion: CurrentVersion, NewVersion: null,
                    Message: "You're on the latest version.");
            }

            var newVersion = update.TargetFullRelease.Version.ToString();
            _logger.LogInformation("Update available: v{Version}", newVersion);
            return new UpdateCheckResult(
                Ok: true, Installed: true, Available: true,
                CurrentVersion: CurrentVersion, NewVersion: newVersion,
                Message: $"Update available: v{newVersion}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            _available = null;
            return new UpdateCheckResult(
                Ok: false, Installed: true, Available: false,
                CurrentVersion: CurrentVersion, NewVersion: null,
                Message: "Update check failed.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Download (stage) the last-discovered update. Returns the local package path so
    /// the Rust host can hand it to Update.exe apply. Re-checks if no update is cached
    /// (e.g. the UI called download without a prior check this session).
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAsync()
    {
        var mgr = Mgr();
        if (!SafeIsInstalled(mgr))
            return new UpdateDownloadResult(Ok: false, PackagePath: null, Version: null, Message: "Not an installed app.");

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var update = _available ?? await mgr!.CheckForUpdatesAsync().ConfigureAwait(false);
            _available = update;
            if (update is null)
                return new UpdateDownloadResult(Ok: false, PackagePath: null, Version: null, Message: "No update to download.");

            _logger.LogInformation("Downloading update v{Version}", update.TargetFullRelease.Version);
            await mgr!.DownloadUpdatesAsync(update).ConfigureAwait(false);

            // Velopack stages the package into the install's packages dir. We don't
            // resolve its path here: `Update.exe apply` with no -p applies the LATEST
            // staged package, which is exactly the one we just downloaded. The Rust
            // host calls that, so PackagePath stays null by design.
            _logger.LogInformation("Update v{Version} downloaded + staged.", update.TargetFullRelease.Version);
            return new UpdateDownloadResult(
                Ok: true, PackagePath: null,
                Version: update.TargetFullRelease.Version.ToString(),
                Message: "Downloaded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update download failed");
            return new UpdateDownloadResult(Ok: false, PackagePath: null, Version: null, Message: "Download failed.");
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed record UpdateCheckResult(
    bool Ok, bool Installed, bool Available,
    string CurrentVersion, string? NewVersion, string Message);

public sealed record UpdateDownloadResult(
    bool Ok, string? PackagePath, string? Version, string Message);

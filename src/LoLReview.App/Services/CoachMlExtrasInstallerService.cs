#nullable enable

using System.IO.Compression;
using System.Net.Http;
using LoLReview.Core.Data;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.Services;

/// <summary>
/// Downloads and manages the optional "coach-ml" extras pack (torch +
/// sentence-transformers + hdbscan). The pack is a site-packages tree
/// with no Python runtime — it piggybacks on the core pack's embedded
/// interpreter at load time via coach/_extras.py, which probes for
/// %LOCALAPPDATA%\LoLReviewData\coach\ml\site-packages\ and appends it
/// to sys.path if found.
/// </summary>
public sealed class CoachMlExtrasInstallerService : ICoachMlExtrasInstallerService
{
    private readonly HttpClient _http;
    private readonly ILogger<CoachMlExtrasInstallerService> _logger;

    internal static string MlDir => Path.Combine(AppDataPaths.UserDataRoot, "coach", "ml");
    private static string SitePackagesDir => Path.Combine(MlDir, "site-packages");
    private static string TempDir => Path.Combine(AppDataPaths.UserDataRoot, "coach", "tmp");

    public CoachMlExtrasInstallerService(IHttpClientFactory httpFactory, ILogger<CoachMlExtrasInstallerService> logger)
    {
        _http = httpFactory.CreateClient("CoachInstaller");
        _http.Timeout = TimeSpan.FromMinutes(30);
        _logger = logger;
    }

    public bool IsInstalled => Directory.Exists(SitePackagesDir);

    public string? InstalledVersion =>
        IsInstalled ? CoachPackMetadata.ReadVersion(MlDir) : null;

    public long InstalledSizeBytes =>
        IsInstalled ? CoachPackMetadata.ComputeSizeBytes(MlDir) : 0;

    public async Task<CoachInstallResult> InstallAsync(
        IProgress<CoachInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var version = CoachInstallerService.ResolveAppVersion();
            var packName = $"coach-ml-{version}-win-x64";
            var zipUrl = CoachInstallerService.BuildAssetUrl(version, $"{packName}.zip");
            var shaUrl = CoachInstallerService.BuildAssetUrl(version, $"{packName}.sha256");

            Directory.CreateDirectory(MlDir);
            Directory.CreateDirectory(TempDir);

            progress?.Report(new(CoachInstallStatus.Downloading, 0, $"Downloading coach ML extras v{version}..."));

            var zipPath = Path.Combine(TempDir, $"{packName}.zip");
            var shaPath = Path.Combine(TempDir, $"{packName}.sha256");

            try
            {
                await DownloadWithProgressAsync(zipUrl, zipPath, progress, cancellationToken);
                await DownloadAsync(shaUrl, shaPath, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                var msg = $"Could not download coach ML extras from {zipUrl}. " +
                          $"Check your internet connection. ({ex.Message})";
                _logger.LogWarning(ex, "Coach ML extras download failed");
                return new CoachInstallResult(false, null, msg);
            }

            progress?.Report(new(CoachInstallStatus.Verifying, 90, "Verifying download..."));

            if (!await CoachInstallerService.VerifyShaAsync(zipPath, shaPath))
            {
                return new CoachInstallResult(false, null,
                    "Downloaded ML pack failed SHA-256 verification. Try again.");
            }

            progress?.Report(new(CoachInstallStatus.Verifying, 95, "Extracting..."));

            // Blow away the previous ML dir — a stale site-packages from
            // an earlier version risks ABI mismatch against the core
            // pack's embedded Python.
            if (Directory.Exists(MlDir))
            {
                try { Directory.Delete(MlDir, recursive: true); } catch { }
            }
            Directory.CreateDirectory(MlDir);
            ZipFile.ExtractToDirectory(zipPath, MlDir, overwriteFiles: true);

            try { File.Delete(zipPath); } catch { }
            try { File.Delete(shaPath); } catch { }

            if (!Directory.Exists(SitePackagesDir))
            {
                return new CoachInstallResult(false, null,
                    "Extraction succeeded but site-packages/ was not found in the pack. " +
                    "This is a packaging bug.");
            }

            progress?.Report(new(CoachInstallStatus.Ready, 100, "Installed."));
            _logger.LogInformation("Coach ML extras v{Version} installed to {Path}", version, MlDir);
            return new CoachInstallResult(true, MlDir, null);
        }
        catch (OperationCanceledException)
        {
            return new CoachInstallResult(false, null, "Install cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coach ML extras install failed");
            return new CoachInstallResult(false, null, ex.Message);
        }
    }

    public Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(MlDir))
            {
                Directory.Delete(MlDir, recursive: true);
                _logger.LogInformation("Coach ML extras uninstalled from {Path}", MlDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coach ML extras uninstall encountered issues");
        }
        return Task.CompletedTask;
    }

    private async Task DownloadWithProgressAsync(
        string url, string destination,
        IProgress<CoachInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var dst = File.Create(destination);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            read += n;
            if (total > 0)
            {
                var pct = Math.Min(85, (int)((double)read / total * 85));
                progress?.Report(new(CoachInstallStatus.Downloading, pct, $"Downloading... {read / 1024 / 1024} MB"));
            }
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await File.WriteAllBytesAsync(destination, content, cancellationToken);
    }
}

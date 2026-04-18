#nullable enable

using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using LoLReview.Core.Data;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.Services;

/// <summary>
/// Downloads and manages the coach sidecar installation. Opt-in per plan.
///
/// The sidecar is published as a separate GitHub release asset and
/// downloaded into %LOCALAPPDATA%\LoLReviewData\coach\bin\ when the user
/// enables coaching in Settings.
///
/// NOTE (overnight run, 2026-04-18): the release pipeline does not yet
/// publish the sidecar asset. This service is implemented end-to-end but
/// the download URL and manifest are placeholders. @samif0 needs to:
///   1. Package coach/ as a pyinstaller .exe in a new GitHub Actions job.
///   2. Upload the exe + a version manifest JSON to the release.
///   3. Replace SidecarReleaseUrl below with the real URL.
/// Until step 3, calling InstallAsync returns Success=false with a friendly
/// error explaining the sidecar artifact isn't available yet. The rest of
/// the coach plumbing is ready.
/// </summary>
public sealed class CoachInstallerService : ICoachInstallerService
{
    // TODO(phase-6): replace with real release URL once pipeline publishes.
    private const string SidecarReleaseUrl =
        "https://github.com/samif0/lol-review/releases/latest/download/coach-sidecar-win-x64.zip";
    private const string SidecarReleaseShaUrl =
        "https://github.com/samif0/lol-review/releases/latest/download/coach-sidecar-win-x64.sha256";
    private const string SidecarExecutableName = "coach.exe";

    private readonly HttpClient _http;
    private readonly ILogger<CoachInstallerService> _logger;

    private static string CoachBinDir => Path.Combine(AppDataPaths.UserDataRoot, "coach", "bin");
    private static string CoachTempDir => Path.Combine(AppDataPaths.UserDataRoot, "coach", "tmp");

    public CoachInstallerService(IHttpClientFactory httpFactory, ILogger<CoachInstallerService> logger)
    {
        _http = httpFactory.CreateClient("CoachInstaller");
        _http.Timeout = TimeSpan.FromMinutes(20);
        _logger = logger;
    }

    public bool IsInstalled
    {
        get
        {
            var exe = SidecarExecutablePath;
            return exe is not null && File.Exists(exe);
        }
    }

    public string? SidecarExecutablePath
    {
        get
        {
            try
            {
                return Path.Combine(CoachBinDir, SidecarExecutableName);
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<CoachInstallResult> InstallAsync(
        IProgress<CoachInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(CoachBinDir);
            Directory.CreateDirectory(CoachTempDir);

            progress?.Report(new(CoachInstallStatus.Downloading, 0, "Downloading coach sidecar..."));

            var zipPath = Path.Combine(CoachTempDir, "coach-sidecar.zip");
            var shaPath = Path.Combine(CoachTempDir, "coach-sidecar.sha256");

            try
            {
                await DownloadWithProgressAsync(
                    SidecarReleaseUrl, zipPath, progress, cancellationToken);
                await DownloadAsync(SidecarReleaseShaUrl, shaPath, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                var msg = "Coach sidecar release asset is not available yet. " +
                          "The coach code is ready but the download pipeline hasn't published a build. " +
                          "See CoachInstallerService.cs for setup instructions.";
                _logger.LogWarning(ex, msg);
                return new CoachInstallResult(false, null, msg);
            }

            progress?.Report(new(CoachInstallStatus.Verifying, 90, "Verifying download..."));

            if (!await VerifyShaAsync(zipPath, shaPath))
            {
                return new CoachInstallResult(false, null, "SHA256 verification failed.");
            }

            progress?.Report(new(CoachInstallStatus.Verifying, 95, "Extracting..."));
            ZipFile.ExtractToDirectory(zipPath, CoachBinDir, overwriteFiles: true);

            try { File.Delete(zipPath); } catch { }
            try { File.Delete(shaPath); } catch { }

            var exe = SidecarExecutablePath;
            if (exe is null || !File.Exists(exe))
            {
                return new CoachInstallResult(false, null,
                    $"Extraction succeeded but {SidecarExecutableName} was not found in the archive.");
            }

            progress?.Report(new(CoachInstallStatus.Ready, 100, "Installed."));
            _logger.LogInformation("Coach sidecar installed to {Path}", exe);
            return new CoachInstallResult(true, exe, null);
        }
        catch (OperationCanceledException)
        {
            return new CoachInstallResult(false, null, "Install cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coach sidecar install failed");
            return new CoachInstallResult(false, null, ex.Message);
        }
    }

    public Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(CoachBinDir))
            {
                Directory.Delete(CoachBinDir, recursive: true);
                _logger.LogInformation("Coach sidecar uninstalled from {Path}", CoachBinDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coach sidecar uninstall encountered issues");
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

    private static async Task<bool> VerifyShaAsync(string filePath, string shaFilePath)
    {
        try
        {
            var expected = (await File.ReadAllTextAsync(shaFilePath)).Trim().Split(' ')[0].ToLowerInvariant();
            using var sha = SHA256.Create();
            await using var stream = File.OpenRead(filePath);
            var hash = await sha.ComputeHashAsync(stream);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            return expected == actual;
        }
        catch
        {
            return false;
        }
    }
}

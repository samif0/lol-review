#nullable enable

using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using LoLReview.Core.Data;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.Services;

/// <summary>
/// Downloads and manages the "coach-core" sidecar pack: an embedded
/// Python 3.12 runtime + the coach/ package + lightweight deps
/// (fastapi, httpx, numpy, ...). Required for any coach feature.
///
/// The optional "coach-ml" pack (torch + sentence-transformers +
/// hdbscan) is managed separately by <see cref="CoachMlExtrasInstallerService"/>.
///
/// The pack URL is derived from the running app version so the client
/// always pulls a core pack tagged to the same release it came from.
/// Downloads go into %LOCALAPPDATA%\LoLReviewData\coach\core\ and the
/// launcher entry point is runtime\python.exe running `-m coach.main`
/// (matching what coach.cmd in the pack does; we skip the .cmd shim
/// to avoid an extra cmd.exe process).
/// </summary>
public sealed class CoachInstallerService : ICoachInstallerService
{
    private const string RepoSlug = "samif0/lol-review";

    private readonly HttpClient _http;
    private readonly ILogger<CoachInstallerService> _logger;

    internal static string CoreDir => Path.Combine(AppDataPaths.UserDataRoot, "coach", "core");
    private static string TempDir => Path.Combine(AppDataPaths.UserDataRoot, "coach", "tmp");

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

    /// <summary>
    /// Absolute path to the embedded Python interpreter inside the
    /// installed core pack. <see cref="CoachSidecarService"/> launches
    /// this with `-u -X utf8 -m coach.main --port N` (equivalent to the
    /// coach.cmd shim shipped in the pack).
    /// </summary>
    public string? SidecarExecutablePath
    {
        get
        {
            try
            {
                return Path.Combine(CoreDir, "runtime", "python.exe");
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Path to the root of the installed core pack, used by the sidecar
    /// service to set the working directory and by the ML pack
    /// installer to co-locate extras.
    /// </summary>
    public string? CorePackRoot => Directory.Exists(CoreDir) ? CoreDir : null;

    public async Task<CoachInstallResult> InstallAsync(
        IProgress<CoachInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var version = ResolveAppVersion();
            var packName = $"coach-core-{version}-win-x64";
            var zipUrl = BuildAssetUrl(version, $"{packName}.zip");
            var shaUrl = BuildAssetUrl(version, $"{packName}.sha256");

            Directory.CreateDirectory(CoreDir);
            Directory.CreateDirectory(TempDir);

            progress?.Report(new(CoachInstallStatus.Downloading, 0, $"Downloading coach core v{version}..."));

            var zipPath = Path.Combine(TempDir, $"{packName}.zip");
            var shaPath = Path.Combine(TempDir, $"{packName}.sha256");

            try
            {
                await DownloadWithProgressAsync(zipUrl, zipPath, progress, cancellationToken);
                await DownloadAsync(shaUrl, shaPath, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                var msg = $"Could not download coach core pack from {zipUrl}. " +
                          $"Check your internet connection or visit the release page. ({ex.Message})";
                _logger.LogWarning(ex, "Coach core pack download failed");
                return new CoachInstallResult(false, null, msg);
            }

            progress?.Report(new(CoachInstallStatus.Verifying, 90, "Verifying download..."));

            if (!await VerifyShaAsync(zipPath, shaPath))
            {
                return new CoachInstallResult(false, null,
                    "Downloaded pack failed SHA-256 verification. This usually means the download was corrupted — try again.");
            }

            progress?.Report(new(CoachInstallStatus.Verifying, 95, "Extracting..."));

            // Wipe the existing core dir so leftover files from a
            // previous version can't interfere with the new one.
            if (Directory.Exists(CoreDir))
            {
                try { Directory.Delete(CoreDir, recursive: true); } catch { }
            }
            Directory.CreateDirectory(CoreDir);
            ZipFile.ExtractToDirectory(zipPath, CoreDir, overwriteFiles: true);

            try { File.Delete(zipPath); } catch { }
            try { File.Delete(shaPath); } catch { }

            var exe = SidecarExecutablePath;
            if (exe is null || !File.Exists(exe))
            {
                return new CoachInstallResult(false, null,
                    "Extraction succeeded but the Python runtime was not found in the pack. " +
                    "This is a packaging bug — please report it.");
            }

            progress?.Report(new(CoachInstallStatus.Ready, 100, "Installed."));
            _logger.LogInformation("Coach core pack v{Version} installed to {Path}", version, CoreDir);
            return new CoachInstallResult(true, exe, null);
        }
        catch (OperationCanceledException)
        {
            return new CoachInstallResult(false, null, "Install cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coach core install failed");
            return new CoachInstallResult(false, null, ex.Message);
        }
    }

    public Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(CoreDir))
            {
                Directory.Delete(CoreDir, recursive: true);
                _logger.LogInformation("Coach core pack uninstalled from {Path}", CoreDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coach core uninstall encountered issues");
        }
        return Task.CompletedTask;
    }

    internal static string ResolveAppVersion()
    {
        // Release builds have the [assembly: AssemblyFileVersion] stamped
        // by the CI workflow's csproj rewrite step. Dev builds inherit
        // whatever the csproj currently holds.
        var asm = Assembly.GetExecutingAssembly();
        var fileVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";

        // InformationalVersion can carry +gitsha suffixes ("2.8.1+abc123");
        // strip anything after + so the URL matches the release tag.
        var plus = fileVersion.IndexOf('+');
        if (plus > 0) fileVersion = fileVersion[..plus];

        // Version.ToString() includes a trailing ".0" for unused fields
        // (e.g. "2.8.1.0"). The release tag uses three components, so
        // drop any trailing ".0" component we don't need.
        while (fileVersion.EndsWith(".0") && fileVersion.Count(c => c == '.') > 2)
        {
            fileVersion = fileVersion[..^2];
        }

        return fileVersion;
    }

    internal static string BuildAssetUrl(string version, string fileName) =>
        $"https://github.com/{RepoSlug}/releases/download/v{version}/{fileName}";

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

    internal static async Task<bool> VerifyShaAsync(string filePath, string shaFilePath)
    {
        try
        {
            // .sha256 format written by our build scripts:
            //   <hex>  *<filename>
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

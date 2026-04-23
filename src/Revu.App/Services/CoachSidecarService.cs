#nullable enable

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Revu.App.Helpers;
using Revu.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Revu.App.Services;

/// <summary>
/// Lifecycle manager for the Python coach sidecar. Starts the child process
/// after DI is wired, polls /health, stops on app exit.
///
/// Per COACH_PLAN.md §7 Phase 0. Mirrors the GameMonitorService patterns
/// for background service lifecycle.
///
/// The sidecar is opt-in: if it is not installed (no bin directory in
/// coach/, checked via CoachInstallerService), this service is a no-op.
/// </summary>
public sealed class CoachSidecarService : IHostedService, IAsyncDisposable
{
    private const string HealthPath = "/health";
    private const int DefaultPort = 5577;
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupHealthTimeout = TimeSpan.FromSeconds(60);

    private readonly ICoachInstallerService _installer;
    private readonly ICoachCredentialStore _credentials;
    private readonly ILogger<CoachSidecarService> _logger;
    private readonly HttpClient _http;
    private Process? _process;
    private CancellationTokenSource? _healthPollCts;
    private Task? _healthPollTask;
    private int _discoveredPort = DefaultPort;

    public int Port => _discoveredPort;

    public bool IsRunning => _process is { HasExited: false };

    public bool IsHealthy { get; private set; }

    public CoachSidecarService(
        ICoachInstallerService installer,
        ICoachCredentialStore credentials,
        IHttpClientFactory httpFactory,
        ILogger<CoachSidecarService> logger)
    {
        _installer = installer;
        _credentials = credentials;
        _logger = logger;
        _http = httpFactory.CreateClient("CoachSidecar");
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _discoveredPort = ResolveConfiguredPort();

        // Passive on IHostedService startup — attach to an existing sidecar if
        // one is already running (e.g. from a dev python -m coach.main), but
        // do NOT launch a new one. Lazy-start is triggered later by
        // EnsureSidecarRunningAsync from the Coach page or objective button.
        if (await CheckHealthAsync(cancellationToken))
        {
            AppDiagnostics.WriteVerbose("startup.log",
                $"CoachSidecarService: external sidecar detected on port {_discoveredPort}, attaching");
            await InjectStoredCredentialsAsync(cancellationToken).ConfigureAwait(false);
            _healthPollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _healthPollTask = Task.Run(() => PollHealthAsync(_healthPollCts.Token), _healthPollCts.Token);
            return;
        }

        AppDiagnostics.WriteVerbose("startup.log",
            "CoachSidecarService: no sidecar on port at startup; will launch on first coach usage");
    }

    /// <summary>
    /// Lazy-start: launch the sidecar if one isn't already running, inject
    /// credentials, and wait for it to be healthy. Safe to call repeatedly —
    /// a semaphore prevents concurrent launches, and if a healthy sidecar is
    /// already attached we return immediately.
    ///
    /// Returns true if the sidecar is healthy (or became healthy) by the end
    /// of the call; false if startup failed.
    /// </summary>
    public async Task<bool> EnsureSidecarRunningAsync(CancellationToken cancellationToken = default)
    {
        await _ensureLock.WaitAsync(cancellationToken);
        try
        {
            if (IsHealthy)
            {
                return true;
            }

            if (await CheckHealthAsync(cancellationToken))
            {
                // Something is already there. Attach and inject credentials.
                await InjectStoredCredentialsAsync(cancellationToken).ConfigureAwait(false);
                _healthPollCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _healthPollTask ??= Task.Run(() => PollHealthAsync(_healthPollCts.Token), _healthPollCts.Token);
                return true;
            }

            // Launch. Priority order:
            //   1. Installed sidecar exe (release channel path)
            //   2. Dev fallback: python.exe in coach/.venv + running `-m coach.main`
            var launched = TryLaunchInstalledSidecar() || TryLaunchDevSidecar();
            if (!launched)
            {
                AppDiagnostics.WriteVerbose("startup.log",
                    "CoachSidecarService: no installed or dev sidecar available; cannot start");
                return false;
            }

            await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
            if (!IsHealthy)
            {
                AppDiagnostics.WriteVerbose("startup.log",
                    "CoachSidecarService: launched sidecar but it never became healthy");
                return false;
            }

            await InjectStoredCredentialsAsync(cancellationToken).ConfigureAwait(false);
            _healthPollCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _healthPollTask ??= Task.Run(() => PollHealthAsync(_healthPollCts.Token), _healthPollCts.Token);
            return true;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private bool TryLaunchInstalledSidecar()
    {
        if (!_installer.IsInstalled) return false;
        var executable = _installer.SidecarExecutablePath;
        if (executable is null || !File.Exists(executable)) return false;

        // The installed pack ships an embedded python.exe + the coach/
        // package on sys.path (via python312._pth). This is equivalent to
        // the coach.cmd shim bundled in the pack; we launch python
        // directly to skip an extra cmd.exe process.
        var args = $"-u -X utf8 -m coach.main --port {_discoveredPort} --log-level info";
        var packRoot = Path.GetDirectoryName(Path.GetDirectoryName(executable));
        return TryStartProcess(executable, args, workingDir: packRoot);
    }

    private bool TryLaunchDevSidecar()
    {
        // Dev fallback: walk up from the app exe to find the repo root, then
        // look for coach/.venv/Scripts/python.exe + coach/coach/main.py.
        // This lets devs run the coach without a published release.
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return false;

        var venvPython = Path.Combine(repoRoot, "coach", ".venv", "Scripts", "python.exe");
        var coachPkg = Path.Combine(repoRoot, "coach", "coach", "main.py");
        if (!File.Exists(venvPython) || !File.Exists(coachPkg))
        {
            return false;
        }

        var coachDir = Path.Combine(repoRoot, "coach");
        return TryStartProcess(
            venvPython,
            "-u -X utf8 -m coach.main",
            workingDir: coachDir);
    }

    private static string? FindRepoRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                // Repo marker: presence of coach/coach/main.py relative to this dir
                var marker = Path.Combine(dir.FullName, "coach", "coach", "main.py");
                if (File.Exists(marker))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // Swallow — just means we can't find it.
        }
        return null;
    }

    private bool TryStartProcess(string fileName, string arguments, string? workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        if (workingDir is not null) psi.WorkingDirectory = workingDir;

        try
        {
            _process = Process.Start(psi);
            if (_process is null)
            {
                AppDiagnostics.WriteVerbose("startup.log",
                    $"CoachSidecarService: Process.Start returned null for {fileName}");
                return false;
            }

            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogDebug("[coach] {Line}", e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogInformation("[coach] {Line}", e.Data);
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            AppDiagnostics.WriteVerbose("startup.log",
                $"CoachSidecarService: launched sidecar pid={_process.Id} on port {_discoveredPort} via {Path.GetFileName(fileName)}");
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteVerbose("startup.log",
                $"CoachSidecarService: failed to start sidecar via {Path.GetFileName(fileName)}: {ex.Message}");
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _healthPollCts?.Cancel();
            if (_healthPollTask is not null)
            {
                try
                {
                    await _healthPollTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            if (_process is not null && !_process.HasExited)
            {
                _logger.LogInformation("Stopping coach sidecar pid={Pid}", _process.Id);
                try
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to stop coach sidecar cleanly");
                }
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _healthPollCts?.Dispose();
            _healthPollCts = null;
            IsHealthy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _http.Dispose();
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync(
                new Uri($"http://127.0.0.1:{_discoveredPort}{HealthPath}"),
                cancellationToken);
            IsHealthy = response.IsSuccessStatusCode;
            return IsHealthy;
        }
        catch
        {
            IsHealthy = false;
            return false;
        }
    }

    private async Task InjectStoredCredentialsAsync(CancellationToken cancellationToken)
    {
        var hasGoogle = _credentials.HasGoogleAiApiKey();
        var hasRouter = _credentials.HasOpenRouterApiKey();
        AppDiagnostics.WriteVerbose("startup.log",
            $"CoachSidecarService: checking stored credentials google_ai={hasGoogle} openrouter={hasRouter}");

        var googleKey = _credentials.GetGoogleAiApiKey();
        var openRouterKey = _credentials.GetOpenRouterApiKey();

        if (string.IsNullOrEmpty(googleKey) && string.IsNullOrEmpty(openRouterKey))
        {
            AppDiagnostics.WriteVerbose("startup.log", "CoachSidecarService: no stored coach credentials to inject");
            return;
        }

        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(googleKey))
        {
            body["google_ai"] = new Dictionary<string, object?> { ["api_key"] = googleKey };
        }
        if (!string.IsNullOrEmpty(openRouterKey))
        {
            body["openrouter"] = new Dictionary<string, object?> { ["api_key"] = openRouterKey };
        }

        try
        {
            var response = await _http.PostAsJsonAsync(
                new Uri($"http://127.0.0.1:{_discoveredPort}/config"),
                body,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                AppDiagnostics.WriteVerbose("startup.log",
                    "CoachSidecarService: injected stored credentials into sidecar");
            }
            else
            {
                AppDiagnostics.WriteVerbose("startup.log",
                    $"CoachSidecarService: sidecar rejected credential injection: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteVerbose("startup.log",
                $"CoachSidecarService: credential injection failed: {ex.Message}");
        }
    }

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(StartupHealthTimeout);

        while (!cts.IsCancellationRequested)
        {
            if (await CheckHealthAsync(cts.Token))
            {
                _logger.LogInformation("Coach sidecar is healthy on port {Port}", _discoveredPort);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
        }

        _logger.LogWarning("Coach sidecar did not become healthy within {Timeout}s", StartupHealthTimeout.TotalSeconds);
    }

    private async Task PollHealthAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HealthPollInterval, cancellationToken).ContinueWith(_ => { }, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            await CheckHealthAsync(cancellationToken);
        }
    }

    private static int ResolveConfiguredPort()
    {
        try
        {
            var configPath = Path.Combine(AppDataPaths.UserDataRoot, "coach_config.json");
            if (!File.Exists(configPath))
                return DefaultPort;

            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("port", out var portProp) &&
                portProp.TryGetInt32(out var port))
            {
                return port;
            }
        }
        catch
        {
            // Fall through to default.
        }
        return DefaultPort;
    }
}

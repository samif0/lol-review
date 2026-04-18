#nullable enable

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using LoLReview.App.Helpers;
using LoLReview.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.Services;

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _discoveredPort = ResolveConfiguredPort();

        if (!_installer.IsInstalled)
        {
            // Not installed via the bundled path, but the user may be running
            // the sidecar manually (dev mode). Probe the configured port; if
            // something responds to /health, treat it as our sidecar and still
            // inject credentials + start health polling.
            AppDiagnostics.WriteVerbose("startup.log",
                "CoachSidecarService: installer reports not-installed; probing port for external sidecar");
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
                "CoachSidecarService: no sidecar found on port; skipping (enable via Settings)");
            return;
        }

        var executable = _installer.SidecarExecutablePath;
        if (executable is null || !File.Exists(executable))
        {
            _logger.LogWarning("Coach installer reports installed but sidecar exe missing at {Path}", executable);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"--port {_discoveredPort} --log-level info",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        try
        {
            _process = Process.Start(psi);
            if (_process is null)
            {
                _logger.LogError("Failed to start coach sidecar process");
                return;
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

            _logger.LogInformation("Coach sidecar started on port {Port}, pid={Pid}", _discoveredPort, _process.Id);

            // Wait for first healthy /health before proceeding.
            await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);

            // Inject stored API keys into the sidecar once it's up.
            AppDiagnostics.WriteVerbose("startup.log",
                "CoachSidecarService: sidecar healthy, starting credential injection");
            await InjectStoredCredentialsAsync(cancellationToken).ConfigureAwait(false);

            _healthPollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _healthPollTask = Task.Run(() => PollHealthAsync(_healthPollCts.Token), _healthPollCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coach sidecar failed to start");
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

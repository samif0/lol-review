#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Services;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

public sealed partial class CoachSettingsViewModel : ObservableObject
{
    private readonly ICoachInstallerService _installer;
    private readonly ICoachApiClient _api;
    private readonly CoachSidecarService _sidecar;
    private readonly ILogger<CoachSettingsViewModel> _logger;

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _installProgress;
    [ObservableProperty] private string _installStatus = "";
    [ObservableProperty] private string _selectedProvider = "ollama";
    [ObservableProperty] private string _ollamaModel = "gemma4:e4b";
    [ObservableProperty] private string _ollamaVisionModel = "gemma4:e4b";
    [ObservableProperty] private string _ollamaBaseUrl = "http://localhost:11434";
    [ObservableProperty] private string _googleAiModel = "gemma-3-27b-it";
    [ObservableProperty] private string _googleAiApiKey = "";
    [ObservableProperty] private string _openRouterModel = "google/gemma-3-27b-it";
    [ObservableProperty] private string _openRouterApiKey = "";
    [ObservableProperty] private string _testPromptText = "Name one League of Legends champion.";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _isTesting;

    public IReadOnlyList<string> ProviderOptions { get; } =
        ["ollama", "google_ai", "openrouter"];

    public CoachSettingsViewModel(
        ICoachInstallerService installer,
        ICoachApiClient api,
        CoachSidecarService sidecar,
        ILogger<CoachSettingsViewModel> logger)
    {
        _installer = installer;
        _api = api;
        _sidecar = sidecar;
        _logger = logger;

        IsInstalled = _installer.IsInstalled;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsInstalling) return;
        IsInstalling = true;
        InstallProgress = 0;
        InstallStatus = "Starting...";

        try
        {
            var progress = new Progress<CoachInstallProgress>(p =>
            {
                InstallProgress = p.PercentComplete;
                InstallStatus = p.Message ?? p.Status.ToString();
            });

            var result = await _installer.InstallAsync(progress);
            if (result.Success)
            {
                IsInstalled = true;
                InstallStatus = "Installed. Restart the app to start the coach.";
            }
            else
            {
                InstallStatus = $"Install failed: {result.Error}";
                _logger.LogWarning("Coach install failed: {Error}", result.Error);
            }
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (IsInstalling) return;
        IsInstalling = true;
        try
        {
            await _sidecar.StopAsync(CancellationToken.None);
            await _installer.UninstallAsync();
            IsInstalled = false;
            InstallStatus = "Uninstalled.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        var update = new CoachConfigUpdate(
            Provider: SelectedProvider,
            Ollama: new CoachOllamaConfig(
                BaseUrl: OllamaBaseUrl,
                Model: OllamaModel,
                VisionModel: OllamaVisionModel),
            GoogleAi: new CoachHostedConfig(
                Model: GoogleAiModel,
                ApiKey: string.IsNullOrWhiteSpace(GoogleAiApiKey) ? null : GoogleAiApiKey),
            OpenRouter: new CoachHostedConfig(
                Model: OpenRouterModel,
                ApiKey: string.IsNullOrWhiteSpace(OpenRouterApiKey) ? null : OpenRouterApiKey));

        var ok = await _api.UpdateConfigAsync(update);
        TestResult = ok ? "Config saved." : "Config save failed. Is the sidecar running?";
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        TestResult = "Testing...";
        try
        {
            var response = await _api.TestPromptAsync(TestPromptText);
            if (response is null)
            {
                TestResult = "No response. Check the sidecar is running and the provider is configured.";
                return;
            }
            TestResult = $"[{response.Provider} / {response.Model} / {response.LatencyMs}ms]\n{response.Text}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}

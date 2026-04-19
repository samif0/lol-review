#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Services;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

public sealed partial class CoachSettingsViewModel : ObservableObject
{
    private readonly ICoachInstallerService _installer;
    private readonly ICoachMlExtrasInstallerService _mlInstaller;
    private readonly ICoachApiClient _api;
    private readonly ICoachCredentialStore _credentials;
    private readonly CoachSidecarService _sidecar;
    private readonly ILogger<CoachSettingsViewModel> _logger;

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _installProgress;
    [ObservableProperty] private string _installStatus = "";
    [ObservableProperty] private bool _isMlInstalled;
    [ObservableProperty] private bool _isInstallingMl;
    [ObservableProperty] private double _mlInstallProgress;
    [ObservableProperty] private string _mlInstallStatus = "";
    [ObservableProperty] private bool _isLocalProvider;
    [ObservableProperty] private string _providerHint = HostedHint;
    [ObservableProperty] private string _selectedProvider = "google_ai";

    private const string HostedHint =
        "Hosted provider. Install the coach runtime below, then paste an API key and click Save coach config.";
    private const string LocalHint =
        "Local provider (Ollama). Requires Ollama running externally. Install the coach runtime below.";
    [ObservableProperty] private string _ollamaModel = "gemma4:e4b";
    [ObservableProperty] private string _ollamaVisionModel = "gemma4:e4b";
    [ObservableProperty] private string _ollamaBaseUrl = "http://localhost:11434";
    [ObservableProperty] private string _googleAiModel = "gemini-2.5-flash";
    [ObservableProperty] private string _googleAiApiKey = "";
    [ObservableProperty] private bool _googleAiKeyStored;
    [ObservableProperty] private string _openRouterModel = "google/gemma-3-27b-it";
    [ObservableProperty] private string _openRouterApiKey = "";
    [ObservableProperty] private bool _openRouterKeyStored;
    [ObservableProperty] private string _testPromptText = "Name one League of Legends champion.";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _isTesting;

    public IReadOnlyList<string> ProviderOptions { get; } =
        ["ollama", "google_ai", "openrouter"];

    public CoachSettingsViewModel(
        ICoachInstallerService installer,
        ICoachMlExtrasInstallerService mlInstaller,
        ICoachApiClient api,
        ICoachCredentialStore credentials,
        CoachSidecarService sidecar,
        ILogger<CoachSettingsViewModel> logger)
    {
        _installer = installer;
        _mlInstaller = mlInstaller;
        _api = api;
        _credentials = credentials;
        _sidecar = sidecar;
        _logger = logger;

        IsInstalled = _installer.IsInstalled;
        IsMlInstalled = _mlInstaller.IsInstalled;
        GoogleAiKeyStored = _credentials.HasGoogleAiApiKey();
        OpenRouterKeyStored = _credentials.HasOpenRouterApiKey();
        UpdateProviderHint();
    }

    partial void OnSelectedProviderChanged(string value) => UpdateProviderHint();

    private void UpdateProviderHint()
    {
        IsLocalProvider = string.Equals(SelectedProvider, "ollama", StringComparison.OrdinalIgnoreCase);
        ProviderHint = IsLocalProvider ? LocalHint : HostedHint;
    }

    [RelayCommand]
    private async Task InstallCoreAsync()
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
                _logger.LogWarning("Coach core install failed: {Error}", result.Error);
            }
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task UninstallCoreAsync()
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
    private async Task InstallMlAsync()
    {
        if (IsInstallingMl) return;
        IsInstallingMl = true;
        MlInstallProgress = 0;
        MlInstallStatus = "Starting...";

        try
        {
            var progress = new Progress<CoachInstallProgress>(p =>
            {
                MlInstallProgress = p.PercentComplete;
                MlInstallStatus = p.Message ?? p.Status.ToString();
            });

            var result = await _mlInstaller.InstallAsync(progress);
            if (result.Success)
            {
                IsMlInstalled = true;
                MlInstallStatus = "Installed. Restart the coach sidecar to activate concept features.";
            }
            else
            {
                MlInstallStatus = $"Install failed: {result.Error}";
                _logger.LogWarning("Coach ML extras install failed: {Error}", result.Error);
            }
        }
        finally
        {
            IsInstallingMl = false;
        }
    }

    [RelayCommand]
    private async Task UninstallMlAsync()
    {
        if (IsInstallingMl) return;
        IsInstallingMl = true;
        try
        {
            await _mlInstaller.UninstallAsync();
            IsMlInstalled = false;
            MlInstallStatus = "Uninstalled. Restart the coach sidecar to take effect.";
        }
        finally
        {
            IsInstallingMl = false;
        }
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        // Persist newly-entered keys to the Windows Credential Manager BEFORE
        // sending to the sidecar, so a sidecar restart can re-inject them.
        // Empty field means "keep existing stored key".
        if (!string.IsNullOrWhiteSpace(GoogleAiApiKey))
        {
            _credentials.SetGoogleAiApiKey(GoogleAiApiKey);
            GoogleAiKeyStored = true;
        }
        if (!string.IsNullOrWhiteSpace(OpenRouterApiKey))
        {
            _credentials.SetOpenRouterApiKey(OpenRouterApiKey);
            OpenRouterKeyStored = true;
        }

        // For the config POST, prefer a freshly-entered key over the stored one.
        var googleKey = string.IsNullOrWhiteSpace(GoogleAiApiKey)
            ? _credentials.GetGoogleAiApiKey()
            : GoogleAiApiKey;
        var openRouterKey = string.IsNullOrWhiteSpace(OpenRouterApiKey)
            ? _credentials.GetOpenRouterApiKey()
            : OpenRouterApiKey;

        var update = new CoachConfigUpdate(
            Provider: SelectedProvider,
            Ollama: new CoachOllamaConfig(
                BaseUrl: OllamaBaseUrl,
                Model: OllamaModel,
                VisionModel: OllamaVisionModel),
            GoogleAi: new CoachHostedConfig(
                Model: GoogleAiModel,
                ApiKey: string.IsNullOrWhiteSpace(googleKey) ? null : googleKey),
            OpenRouter: new CoachHostedConfig(
                Model: OpenRouterModel,
                ApiKey: string.IsNullOrWhiteSpace(openRouterKey) ? null : openRouterKey));

        var ok = await _api.UpdateConfigAsync(update);

        // Clear the text fields after save — the keys now live in the vault
        // and shouldn't be held in UI state.
        GoogleAiApiKey = "";
        OpenRouterApiKey = "";

        TestResult = ok ? "Config saved." : "Config save failed. Is the sidecar running?";
    }

    [RelayCommand]
    private void ClearGoogleAiKey()
    {
        _credentials.SetGoogleAiApiKey(null);
        GoogleAiKeyStored = false;
        GoogleAiApiKey = "";
        TestResult = "Google AI key cleared. Sidecar will use it until restart.";
    }

    [RelayCommand]
    private void ClearOpenRouterKey()
    {
        _credentials.SetOpenRouterApiKey(null);
        OpenRouterKeyStored = false;
        OpenRouterApiKey = "";
        TestResult = "OpenRouter key cleared. Sidecar will use it until restart.";
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

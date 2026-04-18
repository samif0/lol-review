#nullable enable

using Microsoft.Extensions.Logging;
using Windows.Security.Credentials;

namespace LoLReview.App.Services;

/// <summary>
/// Stores coach API keys in the Windows Credential Manager under the
/// resource name 'LoLReview.Coach'. Per-user encrypted at rest.
/// </summary>
public sealed class CoachCredentialStore : ICoachCredentialStore
{
    private const string ResourceName = "LoLReview.Coach";
    private const string GoogleAiKey = "google_ai_api_key";
    private const string OpenRouterKey = "openrouter_api_key";

    private readonly PasswordVault _vault = new();
    private readonly ILogger<CoachCredentialStore> _logger;

    public CoachCredentialStore(ILogger<CoachCredentialStore> logger)
    {
        _logger = logger;
    }

    public string? GetGoogleAiApiKey() => TryGet(GoogleAiKey);
    public string? GetOpenRouterApiKey() => TryGet(OpenRouterKey);

    public bool HasGoogleAiApiKey() => TryGet(GoogleAiKey) is not null;
    public bool HasOpenRouterApiKey() => TryGet(OpenRouterKey) is not null;

    public void SetGoogleAiApiKey(string? key) => Set(GoogleAiKey, key);
    public void SetOpenRouterApiKey(string? key) => Set(OpenRouterKey, key);

    private string? TryGet(string username)
    {
        try
        {
            var cred = _vault.Retrieve(ResourceName, username);
            cred.RetrievePassword();
            return string.IsNullOrEmpty(cred.Password) ? null : cred.Password;
        }
        catch
        {
            // PasswordVault throws COMException when the credential doesn't exist.
            return null;
        }
    }

    private void Set(string username, string? key)
    {
        // Remove any existing credential first.
        try
        {
            var existing = _vault.Retrieve(ResourceName, username);
            _vault.Remove(existing);
        }
        catch
        {
            // Nothing to remove, fine.
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                _vault.Add(new PasswordCredential(ResourceName, username, key));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save credential '{User}' to vault", username);
            }
        }
    }
}

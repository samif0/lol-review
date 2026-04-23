#nullable enable

namespace Revu.App.Services;

/// <summary>
/// Per-user encrypted storage for coach API keys. Backed by Windows
/// Credential Manager (PasswordVault). Keys are NEVER written to
/// coach_config.json or any log file.
/// </summary>
public interface ICoachCredentialStore
{
    /// <summary>Returns the stored key, or null if none.</summary>
    string? GetGoogleAiApiKey();
    string? GetOpenRouterApiKey();

    /// <summary>Saves a key. Pass null or empty to delete the stored key.</summary>
    void SetGoogleAiApiKey(string? key);
    void SetOpenRouterApiKey(string? key);

    /// <summary>True if a key is stored (without exposing it).</summary>
    bool HasGoogleAiApiKey();
    bool HasOpenRouterApiKey();
}

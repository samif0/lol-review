#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoLReview.Core.Data;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.Services;

/// <summary>
/// DPAPI-backed credential store for coach API keys.
///
/// Writes to %LOCALAPPDATA%\LoLReviewData\coach_credentials.bin encrypted
/// with Windows DPAPI at CurrentUser scope. This works reliably in
/// unpackaged WinUI 3 apps (PasswordVault had intermittent failures).
///
/// The file never contains plaintext keys. Only the current Windows user
/// account can decrypt it on the same machine.
/// </summary>
public sealed class CoachCredentialStore : ICoachCredentialStore
{
    private const string GoogleAiKey = "google_ai";
    private const string OpenRouterKey = "openrouter";

    private readonly ILogger<CoachCredentialStore> _logger;
    private readonly object _lock = new();

    private static string FilePath => Path.Combine(AppDataPaths.UserDataRoot, "coach_credentials.bin");

    public CoachCredentialStore(ILogger<CoachCredentialStore> logger)
    {
        _logger = logger;
    }

    public string? GetGoogleAiApiKey() => Read().TryGetValue(GoogleAiKey, out var v) ? v : null;
    public string? GetOpenRouterApiKey() => Read().TryGetValue(OpenRouterKey, out var v) ? v : null;

    public bool HasGoogleAiApiKey() => !string.IsNullOrEmpty(GetGoogleAiApiKey());
    public bool HasOpenRouterApiKey() => !string.IsNullOrEmpty(GetOpenRouterApiKey());

    public void SetGoogleAiApiKey(string? key) => Update(GoogleAiKey, key);
    public void SetOpenRouterApiKey(string? key) => Update(OpenRouterKey, key);

    private Dictionary<string, string> Read()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                var encrypted = File.ReadAllBytes(FilePath);
                var decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(decrypted);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return dict ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read coach credential store; treating as empty.");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }

    private void Update(string field, string? value)
    {
        lock (_lock)
        {
            var dict = Read();
            if (string.IsNullOrWhiteSpace(value))
            {
                dict.Remove(field);
            }
            else
            {
                dict[field] = value;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(dict);
                var plaintext = Encoding.UTF8.GetBytes(json);
                var encrypted = ProtectedData.Protect(plaintext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                File.WriteAllBytes(FilePath, encrypted);
                _logger.LogInformation("Coach credential '{Field}' {Action} in DPAPI store.",
                    field, string.IsNullOrWhiteSpace(value) ? "cleared" : "saved");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write coach credential store");
            }
        }
    }
}

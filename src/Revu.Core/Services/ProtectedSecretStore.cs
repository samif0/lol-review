#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// DPAPI-backed store for local bearer tokens and other credential-like values.
/// Data is encrypted for the current Windows user before being written to disk.
/// </summary>
public sealed class ProtectedSecretStore : IProtectedSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<ProtectedSecretStore> _logger;
    private readonly object _gate = new();
    private readonly string _filePath;

    public ProtectedSecretStore(ILogger<ProtectedSecretStore> logger)
        : this(logger, Path.Combine(AppDataPaths.UserDataRoot, "protected_secrets.bin"))
    {
    }

    internal ProtectedSecretStore(ILogger<ProtectedSecretStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public string? GetSecret(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        lock (_gate)
        {
            var values = ReadUnlocked();
            return values.TryGetValue(name, out var value) ? value : null;
        }
    }

    public void SetSecret(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            ClearSecret(name);
            return;
        }

        lock (_gate)
        {
            var values = ReadUnlocked();
            values[name] = value;
            WriteUnlocked(values);
        }
    }

    public void ClearSecret(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (_gate)
        {
            var values = ReadUnlocked();
            if (values.Remove(name))
            {
                WriteUnlocked(values);
            }
        }
    }

    private Dictionary<string, string> ReadUnlocked()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var encrypted = File.ReadAllBytes(_filePath);
            var decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read protected secret store; treating it as empty.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void WriteUnlocked(Dictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(values, JsonOptions);
            var plaintext = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write protected secret store.");
            throw;
        }
    }
}

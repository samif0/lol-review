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
            try
            {
                var values = ReadUnlocked();
                return values.TryGetValue(name, out var value) ? value : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read may degrade to "no value"; only writers must refuse (see ReadUnlocked).
                _logger.LogWarning(ex, "Protected secret store is unreadable right now; serving no value.");
                return null;
            }
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

    // Short waits for a sharing violation (antivirus, backup or sync tools) to clear.
    private static readonly int[] ReadRetryDelaysMs = [50, 100, 200];

    /// <summary>
    /// Read the whole store. A missing file is an empty store. A file that exists but
    /// cannot be read (sharing violation, IO error) is retried briefly and then THROWS:
    /// SetSecret/ClearSecret rewrite the whole file from this result, so treating a
    /// failed read as empty would erase every other secret (the session token and the
    /// identity mirrors). Content that can never be decrypted or parsed is kept as a
    /// .corrupt copy and then treated as empty, so a damaged store cannot block sign-in.
    /// </summary>
    private Dictionary<string, string> ReadUnlocked()
    {
        byte[] encrypted;
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            try
            {
                using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                encrypted = buffer.ToArray();
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                       && attempt < ReadRetryDelaysMs.Length)
            {
                _logger.LogDebug(ex, "Protected secret store busy; retrying read.");
                Thread.Sleep(ReadRetryDelaysMs[attempt]);
            }
        }

        try
        {
            var decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.LogError(ex, "Protected secret store could not be decrypted; keeping a copy and starting a new one.");
            PreserveUnreadableStore();
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void PreserveUnreadableStore()
    {
        try
        {
            var copy = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMdd_HHmmss}";
            if (!File.Exists(copy)) File.Copy(_filePath, copy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep a copy of the unreadable protected secret store.");
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
            // Atomic write (temp sibling + rename), same pattern as
            // ConfigService.WriteFileAtomic: this file is the designated
            // recovery path when config.json is lost, and a crash mid-write
            // would otherwise corrupt it — ReadUnlocked then treats the store
            // as EMPTY and the login session is silently gone.
            var tmp = _filePath + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write protected secret store.");
            throw;
        }
    }
}

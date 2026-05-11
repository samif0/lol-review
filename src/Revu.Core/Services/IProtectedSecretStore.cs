#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Stores local-only secrets outside config.json. Implementations must avoid
/// logging secret values and should scope decryption to the current Windows user.
/// </summary>
public interface IProtectedSecretStore
{
    string? GetSecret(string name);

    void SetSecret(string name, string value);

    void ClearSecret(string name);
}

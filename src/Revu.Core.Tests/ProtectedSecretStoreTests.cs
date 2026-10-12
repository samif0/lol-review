using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The DPAPI secret store is rewritten whole on every Set/Clear, so a read that fails
/// must never be mistaken for an empty store: that would erase the session token and
/// the identity mirrors on the next config save.
/// </summary>
public sealed class ProtectedSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "secrets-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_dir, "protected_secrets.bin");

    public ProtectedSecretStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ProtectedSecretStore Store() => new(NullLogger<ProtectedSecretStore>.Instance, StorePath);

    [WindowsFact]
    public void SetSecret_WhileTheStoreIsLocked_Throws_AndErasesNothing()
    {
        var store = Store();
        store.SetSecret("riot_session_token", "token");
        store.SetSecret("riot_id", "Player#NA1");

        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.SetSecret("riot_puuid", "puuid"));
            Assert.ThrowsAny<IOException>(() => store.ClearSecret("riot_id"));
            // Reads degrade to "no value" instead of throwing.
            Assert.Null(store.GetSecret("riot_session_token"));
        }

        var reopened = Store();
        Assert.Equal("token", reopened.GetSecret("riot_session_token"));
        Assert.Equal("Player#NA1", reopened.GetSecret("riot_id"));
        Assert.Null(reopened.GetSecret("riot_puuid"));
    }

    [WindowsFact]
    public void UndecryptableStore_IsKeptAsACopy_AndANewStoreStarts()
    {
        File.WriteAllBytes(StorePath, [1, 2, 3, 4, 5]);
        var store = Store();

        store.SetSecret("riot_session_token", "fresh");

        Assert.Equal("fresh", store.GetSecret("riot_session_token"));
        var copy = Assert.Single(Directory.GetFiles(_dir, "protected_secrets.bin.corrupt-*"));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(copy));
    }

    [WindowsFact]
    public void UndecryptableStore_ReadOnly_IsKeptOnce_NotCopiedOnEveryRead()
    {
        // A signed-out user never writes, so only reads reach the bad store. Config loads
        // read it several times a second; the stamps below land in different seconds.
        File.WriteAllBytes(StorePath, [1, 2, 3, 4, 5]);
        var store = Store();

        Assert.Null(store.GetSecret("riot_session_token"));
        Thread.Sleep(1100);
        Assert.Null(store.GetSecret("riot_id"));
        Assert.Null(Store().GetSecret("riot_puuid"));

        var copy = Assert.Single(Directory.GetFiles(_dir, "protected_secrets.bin.corrupt-*"));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(copy));
        Assert.False(File.Exists(StorePath));
    }

    [WindowsFact]
    public void MissingStore_IsEmpty_AndSetCreatesIt()
    {
        var store = Store();
        Assert.Null(store.GetSecret("riot_session_token"));

        store.SetSecret("riot_session_token", "token");
        store.ClearSecret("riot_session_token");

        Assert.True(File.Exists(StorePath));
        Assert.Null(Store().GetSecret("riot_session_token"));
    }
}

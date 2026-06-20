using System.Text.Json;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

public sealed class ConfigServiceProtectedSecretsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    [Fact]
    public async Task LoadAsync_MigratesPlaintextTokensToProtectedStoreAndSanitizesConfigFile()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var original = new AppConfig
        {
            GithubToken = "github-secret",
            RiotSessionToken = "riot-session-secret",
            RiotSessionEmail = "tester@example.com",
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        };
        await File.WriteAllTextAsync(scope.ConfigPath, JsonSerializer.Serialize(original, JsonOptions));

        var service = CreateService(scope.ConfigPath, secrets);
        var loaded = await service.LoadAsync();

        Assert.Equal("github-secret", loaded.GithubToken);
        Assert.Equal("riot-session-secret", loaded.RiotSessionToken);
        Assert.Equal("github-secret", secrets.GetSecret("github_token"));
        Assert.Equal("riot-session-secret", secrets.GetSecret("riot_session_token"));

        var persisted = await File.ReadAllTextAsync(scope.ConfigPath);
        Assert.DoesNotContain("github-secret", persisted);
        Assert.DoesNotContain("riot-session-secret", persisted);
        var sanitized = JsonSerializer.Deserialize<AppConfig>(persisted, JsonOptions)!;
        Assert.Equal("", sanitized.GithubToken);
        Assert.Equal("", sanitized.RiotSessionToken);
        Assert.Equal("tester@example.com", sanitized.RiotSessionEmail);
    }

    [Fact]
    public async Task SaveAsync_WritesSecretsToProtectedStoreAndLeavesConfigSanitized()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await service.SaveAsync(new AppConfig
        {
            GithubToken = "github-secret",
            RiotSessionToken = "riot-session-secret",
            RiotSessionEmail = "tester@example.com",
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        });

        Assert.Equal("github-secret", secrets.GetSecret("github_token"));
        Assert.Equal("riot-session-secret", secrets.GetSecret("riot_session_token"));

        var persisted = await File.ReadAllTextAsync(scope.ConfigPath);
        Assert.DoesNotContain("github-secret", persisted);
        Assert.DoesNotContain("riot-session-secret", persisted);

        var loaded = await service.LoadAsync();
        Assert.Equal("github-secret", loaded.GithubToken);
        Assert.Equal("riot-session-secret", loaded.RiotSessionToken);
    }

    [Fact]
    public async Task SaveAsync_WithEmptyRiotToken_PRESERVESStoredSession()
    {
        // Clobber-fix behavior: a NORMAL save whose RiotSessionToken is empty must NOT
        // delete the stored session token — it means "this writer didn't touch the
        // session". (GithubToken keeps the old clear-on-empty behavior; only the Riot
        // session is protected, because only `verify` ever sets it in-memory and every
        // other read-modify-write handler carries an empty token.)
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await service.SaveAsync(new AppConfig
        {
            GithubToken = "github-secret",
            RiotSessionToken = "riot-session-secret",
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        });

        var loaded = await service.LoadAsync();
        loaded.GithubToken = "";
        loaded.RiotSessionToken = "";          // a stale/non-session writer
        loaded.RiotSessionExpiresAt = 0;       // and a zeroed expiry
        await service.SaveAsync(loaded);

        // GitHub token still clears on empty (unchanged behavior).
        Assert.Null(secrets.GetSecret("github_token"));
        // Riot session token + expiry are PRESERVED, not wiped.
        Assert.Equal("riot-session-secret", secrets.GetSecret("riot_session_token"));
        var reloaded = await service.LoadAsync();
        Assert.Equal("riot-session-secret", reloaded.RiotSessionToken);
        Assert.True(reloaded.RiotSessionExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ClearSessionAsync_ActuallyWipesTheSession()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await service.SaveAsync(new AppConfig
        {
            RiotSessionToken = "riot-session-secret",
            RiotSessionEmail = "user@example.com",
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        });

        await service.ClearSessionAsync();

        Assert.Null(secrets.GetSecret("riot_session_token"));
        var reloaded = await service.LoadAsync();
        Assert.Equal("", reloaded.RiotSessionToken);
        Assert.Equal("", reloaded.RiotSessionEmail);
        Assert.Equal(0, reloaded.RiotSessionExpiresAt);
    }

    [Fact]
    public async Task SaveAsync_MirrorsRiotIdentityIntoProtectedStoreButKeepsPlaintextForUi()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await service.SaveAsync(new AppConfig
        {
            RiotSessionToken = "riot-session-secret",
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            RiotId = "Player#NA1",
            RiotRegion = "na1",
            RiotPuuid = "puuid-123",
        });

        // Mirrored into DPAPI...
        Assert.Equal("Player#NA1", secrets.GetSecret("riot_id"));
        Assert.Equal("na1", secrets.GetSecret("riot_region"));
        Assert.Equal("puuid-123", secrets.GetSecret("riot_puuid"));

        // ...but NOT blanked from plaintext config (the UI reads these for display).
        var persisted = await File.ReadAllTextAsync(scope.ConfigPath);
        var sanitized = JsonSerializer.Deserialize<AppConfig>(persisted, JsonOptions)!;
        Assert.Equal("Player#NA1", sanitized.RiotId);
        Assert.Equal("na1", sanitized.RiotRegion);
        Assert.Equal("puuid-123", sanitized.RiotPuuid);
        // The token IS sanitized (unchanged behavior).
        Assert.Equal("", sanitized.RiotSessionToken);
    }

    [Fact]
    public async Task LoadAsync_RecoversRiotIdentityFromProtectedStoreWhenConfigIsClobbered()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();

        // Simulate the P-020/P-023 clobber: a valid token + identity live in DPAPI,
        // but config.json has had riot_id/region/puuid blanked by an empty-overwrite.
        secrets.SetSecret("riot_session_token", "riot-session-secret");
        secrets.SetSecret("riot_id", "Player#NA1");
        secrets.SetSecret("riot_region", "na1");
        secrets.SetSecret("riot_puuid", "puuid-123");
        var clobbered = new AppConfig
        {
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            RiotId = "",
            RiotRegion = "",
            RiotPuuid = "",
        };
        await File.WriteAllTextAsync(scope.ConfigPath, JsonSerializer.Serialize(clobbered, JsonOptions));

        var service = CreateService(scope.ConfigPath, secrets);
        var loaded = await service.LoadAsync();

        // Identity self-heals from the store → login gate stays satisfied.
        Assert.Equal("Player#NA1", loaded.RiotId);
        Assert.Equal("na1", loaded.RiotRegion);
        Assert.Equal("puuid-123", loaded.RiotPuuid);
        Assert.True(service.HasValidRiotSession);
        Assert.True(service.RiotProxyEnabled);
    }

    [Fact]
    public async Task LoadAsync_PrefersPlaintextRiotIdentityWhenStoreIsEmpty()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();

        // Legacy config predating the mirror: identity only in plaintext, no store copy.
        var legacy = new AppConfig
        {
            RiotSessionToken = "riot-session-secret",
            RiotSessionExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            RiotId = "Legacy#EUW",
            RiotRegion = "euw1",
            RiotPuuid = "legacy-puuid",
        };
        await File.WriteAllTextAsync(scope.ConfigPath, JsonSerializer.Serialize(legacy, JsonOptions));

        var service = CreateService(scope.ConfigPath, secrets);
        var loaded = await service.LoadAsync();

        // Plaintext is used when the store is empty, AND the migrate path seeds the store.
        Assert.Equal("Legacy#EUW", loaded.RiotId);
        Assert.Equal("euw1", loaded.RiotRegion);
        Assert.Equal("legacy-puuid", loaded.RiotPuuid);
        Assert.Equal("Legacy#EUW", secrets.GetSecret("riot_id"));
        Assert.True(service.RiotProxyEnabled);
    }

    private static ConfigService CreateService(string configPath, IProtectedSecretStore secrets) =>
        new(NullLogger<ConfigService>.Instance, secrets, configPath);

    private sealed class TempConfigScope : IDisposable
    {
        private readonly string _root;

        public TempConfigScope()
        {
            _root = Path.Combine(Path.GetTempPath(), "Revu.ConfigService.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            ConfigPath = Path.Combine(_root, "config.json");
        }

        public string ConfigPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeProtectedSecretStore : IProtectedSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? GetSecret(string name) => _values.TryGetValue(name, out var value) ? value : null;

        public void SetSecret(string name, string value) => _values[name] = value;

        public void ClearSecret(string name) => _values.Remove(name);
    }
}

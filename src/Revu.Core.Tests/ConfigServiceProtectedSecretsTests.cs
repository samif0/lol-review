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
    public async Task SaveAsync_ClearsProtectedSecretsWhenConfigTokensAreCleared()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await service.SaveAsync(new AppConfig
        {
            GithubToken = "github-secret",
            RiotSessionToken = "riot-session-secret",
        });

        var loaded = await service.LoadAsync();
        loaded.GithubToken = "";
        loaded.RiotSessionToken = "";
        await service.SaveAsync(loaded);

        Assert.Null(secrets.GetSecret("github_token"));
        Assert.Null(secrets.GetSecret("riot_session_token"));

        var persisted = await File.ReadAllTextAsync(scope.ConfigPath);
        var sanitized = JsonSerializer.Deserialize<AppConfig>(persisted, JsonOptions)!;
        Assert.Equal("", sanitized.GithubToken);
        Assert.Equal("", sanitized.RiotSessionToken);
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

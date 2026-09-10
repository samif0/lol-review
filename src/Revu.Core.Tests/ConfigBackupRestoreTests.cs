using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// v3.10: every config.json write keeps a rolling config.json.bak, and a load
/// that finds config.json gone restores it from that copy instead of silently
/// running on defaults (the 2026-07-10 / 2026-09-05 "settings reset themselves"
/// class, whose deleter was a test suite reaching the live path).
/// </summary>
public sealed class ConfigBackupRestoreTests
{
    [Fact]
    public async Task SaveAsync_KeepsABackupOfThePreviousFile_AndLoadRestoresFromIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "Revu.ConfigBackup.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, new MemorySecretStore(), configPath);

            await service.SaveAsync(new AppConfig { AscentFolder = root, TiltFixMode = true });
            Assert.True(File.Exists(configPath));
            // First write had nothing to back up; the second one does.
            await service.SaveAsync(new AppConfig { AscentFolder = root, TiltFixMode = true, AutoTimelineClippingEnabled = false });
            Assert.True(File.Exists(configPath + ".bak"), "a rolling backup must be kept beside config.json");

            // Something deletes the file (the historical failure). A fresh load
            // must bring the last good copy back rather than fall to defaults.
            File.Delete(configPath);
            var reloaded = await new ConfigService(NullLogger<ConfigService>.Instance, new MemorySecretStore(), configPath).LoadAsync();

            Assert.True(File.Exists(configPath), "config.json must be restored from config.json.bak");
            Assert.Equal(root, reloaded.AscentFolder);
            Assert.True(reloaded.TiltFixMode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SecretMigrationRewrite_LeavesNoPlaintextTokenBehindInTheBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Revu.ConfigBackup.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");
        try
        {
            // A pre-sanitization config with the token in plaintext, plus a stale
            // .bak from an earlier save.
            await File.WriteAllTextAsync(configPath, "{\"riot_session_token\":\"riot-secret-123\",\"riot_session_expires_at\":4102444800}");
            await File.WriteAllTextAsync(configPath + ".bak", "{\"tilt_fix_mode\":true}");

            var loaded = await new ConfigService(NullLogger<ConfigService>.Instance, new MemorySecretStore(), configPath).LoadAsync();

            Assert.Equal("riot-secret-123", loaded.RiotSessionToken); // hydrated from the store
            Assert.DoesNotContain("riot-secret-123", await File.ReadAllTextAsync(configPath));
            Assert.False(File.Exists(configPath + ".bak"), "no copy of the plaintext-token file may survive the migration write");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LoadAsync_FreshInstall_NoBackup_StillFallsToDefaultsQuietly()
    {
        var root = Path.Combine(Path.GetTempPath(), "Revu.ConfigBackup.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var loaded = await new ConfigService(NullLogger<ConfigService>.Instance, new MemorySecretStore(), Path.Combine(root, "config.json")).LoadAsync();
            Assert.Equal("", loaded.AscentFolder);
            Assert.False(File.Exists(Path.Combine(root, "config.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class MemorySecretStore : IProtectedSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? GetSecret(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public void SetSecret(string name, string value) => _values[name] = value;
        public void ClearSecret(string name) => _values.Remove(name);
    }
}

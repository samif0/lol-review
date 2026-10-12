using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;
using System.Text.Json;

namespace Revu.Core.Tests;

/// <summary>
/// The sidebar animation preference was stored but nothing read it. It is gone
/// from AppConfig; a config.json written by an older build must still load every
/// other setting, and the next save drops the retired key.
/// </summary>
public sealed class RetiredSidebarAnimationConfigTests
{
    [Fact]
    public async Task OldConfigWithSidebarAnimationKey_StillLoads_AndTheNextSaveDropsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "Revu.RetiredConfig.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");
        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                sidebar_animation_enabled = false,
                minimize_during_game = false,
                tilt_fix_mode = true,
                riot_id = "existing#player",
            }));

            var service = new ConfigService(NullLogger<ConfigService>.Instance, new MemorySecretStore(), configPath);
            var loaded = await service.LoadAsync();
            Assert.False(loaded.MinimizeDuringGame);
            Assert.True(loaded.TiltFixMode);
            Assert.Equal("existing#player", loaded.RiotId);

            await service.SaveAsync(loaded);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            Assert.False(saved.RootElement.TryGetProperty("sidebar_animation_enabled", out _));
            Assert.False(saved.RootElement.GetProperty("minimize_during_game").GetBoolean());
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

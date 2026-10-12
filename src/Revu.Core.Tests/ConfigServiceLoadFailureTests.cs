using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// H6: config.json yields defaults only when it does not exist. An existing file that
/// cannot be read throws, so a read-modify-write handler can never save defaults over
/// the real settings; corrupt JSON falls back to the .bak before defaults; and
/// UpdateAsync holds one lock across load, change and save.
/// </summary>
public sealed class ConfigServiceLoadFailureTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.ConfigLoad.Tests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "config.json");

    public ConfigServiceLoadFailureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ConfigService Service() => new(NullLogger<ConfigService>.Instance, new MemorySecretStore(), ConfigPath);

    private Task WriteConfigAsync(AppConfig config) =>
        File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));

    [WindowsFact]
    public async Task LockedConfig_LoadThrows_AndAnUpdateNeverWritesDefaults()
    {
        await WriteConfigAsync(new AppConfig { AscentFolder = @"C:\Ascent", TiltFixMode = true, ClipsMaxSizeMb = 4096 });
        var original = await File.ReadAllTextAsync(ConfigPath);
        var service = Service();
        Assert.Equal(@"C:\Ascent", (await service.LoadAsync()).AscentFolder);

        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => service.LoadAsync());
            var mutated = false;
            await Assert.ThrowsAsync<IOException>(() => service.UpdateAsync(cfg => { mutated = true; cfg.TiltFixMode = false; return true; }));
            Assert.False(mutated, "a change must never be applied to a config that failed to load");
            // The last good values stay cached for readers.
            Assert.True(service.TiltFixEnabled);
            Assert.Equal(@"C:\Ascent", service.AscentFolder);
        }

        Assert.Equal(original, await File.ReadAllTextAsync(ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "config.json.corrupt-*"));
    }

    [WindowsFact]
    public async Task LockedConfig_OnFirstAccess_GettersThrowInsteadOfServingDefaults()
    {
        await WriteConfigAsync(new AppConfig { TiltFixMode = true });
        var service = Service();
        using var hold = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.ThrowsAny<IOException>(() => service.TiltFixEnabled);
    }

    [Fact]
    public async Task CorruptConfig_FallsBackToTheBak_AndKeepsTheCorruptCopy()
    {
        await File.WriteAllTextAsync(ConfigPath, "{ \"tilt_fix_mode\": tr");
        await File.WriteAllTextAsync(ConfigPath + ".bak",
            JsonSerializer.Serialize(new AppConfig { AscentFolder = @"C:\Ascent", TiltFixMode = true }, JsonOptions));

        var loaded = await Service().LoadAsync();

        Assert.Equal(@"C:\Ascent", loaded.AscentFolder);
        Assert.True(loaded.TiltFixMode);
        var corrupt = Assert.Single(Directory.GetFiles(_root, "config.json.corrupt-*"));
        Assert.Equal("{ \"tilt_fix_mode\": tr", await File.ReadAllTextAsync(corrupt));
        // The good copy is back in place, so the next load reads it directly.
        Assert.Equal(@"C:\Ascent", (await Service().LoadAsync()).AscentFolder);
    }

    [Fact]
    public async Task CorruptConfig_WithNoUsableBak_FallsToDefaults_AndKeepsTheCorruptCopy()
    {
        await File.WriteAllTextAsync(ConfigPath, "not json");

        var loaded = await Service().LoadAsync();

        Assert.Equal("", loaded.AscentFolder);
        Assert.Single(Directory.GetFiles(_root, "config.json.corrupt-*"));
    }

    [Fact]
    public async Task UpdateAsync_ThatDeclines_LeavesTheFileAndCacheUntouched()
    {
        await WriteConfigAsync(new AppConfig { ClipsMaxSizeMb = 4096 });
        var before = await File.ReadAllTextAsync(ConfigPath);
        var service = Service();

        var saved = await service.UpdateAsync(cfg => { cfg.ClipsMaxSizeMb = 100; return false; });

        Assert.False(saved);
        Assert.Equal(before, await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal(4096, service.ClipsMaxSizeMb);
    }

    [Fact]
    public async Task ConcurrentUpdates_NeverLoseEachOthersChange()
    {
        await WriteConfigAsync(new AppConfig());
        var service = Service();
        var actions = Enumerable.Range(0, 16).Select(i => $"action_{i}").ToArray();

        await Task.WhenAll(actions.Select(action => Task.Run(() =>
            service.UpdateAsync(cfg => { cfg.Keybinds[action] = "K"; return true; }))));

        var reloaded = await Service().LoadAsync();
        foreach (var action in actions) Assert.Equal("K", reloaded.Keybinds[action]);
    }

    [Fact]
    public async Task UpdateAsync_IsVisibleToTheSameInstancesReaders()
    {
        await WriteConfigAsync(new AppConfig { TiltFixMode = false });
        var service = Service();
        Assert.False(service.TiltFixEnabled);

        await service.UpdateAsync(cfg => { cfg.TiltFixMode = true; return true; });

        Assert.True(service.TiltFixEnabled);
    }

    private sealed class MemorySecretStore : IProtectedSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? GetSecret(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public void SetSecret(string name, string value) => _values[name] = value;
        public void ClearSecret(string name) => _values.Remove(name);
    }
}

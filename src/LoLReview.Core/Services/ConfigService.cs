#nullable enable

using System.Text.Json;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

/// <summary>
/// Thread-safe config service that reads/writes %LOCALAPPDATA%\LoLReview\data\config.json.
/// Ported from Python config.py.
/// </summary>
public sealed class ConfigService : IConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoLReview", "data");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    static ConfigService()
    {
        // Migrate config from old location if needed
        var oldConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoLReview", "config.json");
        if (!File.Exists(ConfigFile) && File.Exists(oldConfig))
        {
            Directory.CreateDirectory(ConfigDir);
            File.Copy(oldConfig, ConfigFile);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly ILogger<ConfigService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppConfig? _cached;

    public ConfigService(ILogger<ConfigService> logger)
    {
        _logger = logger;
    }

    // ── IConfigService convenience properties ───────────────────────

    public string GithubToken => GetCached().GithubToken;
    public string? AscentFolder => GetValidatedFolder(GetCached().AscentFolder);
    public bool TiltFixEnabled => GetCached().TiltFixMode;
    public string ClipsFolder => GetValidatedClipsFolder();
    public int ClipsMaxSizeMb => GetCached().ClipsMaxSizeMb;
    public bool BackupEnabled => GetCached().BackupEnabled;
    public string BackupFolder => GetValidatedFolder(GetCached().BackupFolder) ?? "";
    public Dictionary<string, string> Keybinds => GetKeybinds();

    public bool IsAscentEnabled => AscentFolder is not null;

    // ── Load / Save ─────────────────────────────────────────────────

    public async Task<AppConfig> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _cached = await LoadFromDiskAsync().ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await File.WriteAllTextAsync(ConfigFile, json).ConfigureAwait(false);
            _cached = config;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Keybinds ────────────────────────────────────────────────────

    public Dictionary<string, string> GetKeybinds()
    {
        var merged = new Dictionary<string, string>(AppConfig.DefaultKeybinds);
        var saved = GetCached().Keybinds;
        foreach (var (action, key) in saved)
        {
            if (merged.ContainsKey(action) && !string.IsNullOrEmpty(key))
            {
                merged[action] = key;
            }
        }
        return merged;
    }

    // ── Private helpers ─────────────────────────────────────────────

    private AppConfig GetCached()
    {
        if (_cached is not null) return _cached;

        // Synchronous fast-path: load from disk on first access
        _lock.Wait();
        try
        {
            _cached ??= LoadFromDiskSync();
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AppConfig> LoadFromDiskAsync()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = await File.ReadAllTextAsync(ConfigFile).ConfigureAwait(false);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read config from {Path}", ConfigFile);
        }
        return new AppConfig();
    }

    private AppConfig LoadFromDiskSync()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read config from {Path}", ConfigFile);
        }
        return new AppConfig();
    }

    private static string? GetValidatedFolder(string path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            return path;
        return null;
    }

    private string GetValidatedClipsFolder()
    {
        var configured = GetValidatedFolder(GetCached().ClipsFolder);
        if (configured is not null) return configured;

        // Default: LoLReview/clips next to the config dir
        var defaultDir = Path.Combine(ConfigDir, "clips");
        Directory.CreateDirectory(defaultDir);
        return defaultDir;
    }
}

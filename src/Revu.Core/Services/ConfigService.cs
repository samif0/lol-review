#nullable enable

using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Thread-safe config service that reads/writes %LOCALAPPDATA%\RevuData\config.json.
/// Ported from Python config.py.
/// </summary>
public sealed class ConfigService : IConfigService
{
    private static readonly string ConfigDir = Path.GetDirectoryName(AppDataPaths.ConfigPath)!;

    private static readonly string ConfigFile = AppDataPaths.ConfigPath;

    static ConfigService()
    {
        if (File.Exists(ConfigFile))
        {
            return;
        }

        foreach (var legacyConfig in AppDataPaths.EnumerateLegacyConfigPaths())
        {
            if (!File.Exists(legacyConfig))
            {
                continue;
            }

            Directory.CreateDirectory(ConfigDir);
            File.Copy(legacyConfig, ConfigFile);
            break;
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

    public string RiotSessionToken => GetCached().RiotSessionToken;
    public string RiotSessionEmail => GetCached().RiotSessionEmail;
    public long RiotSessionExpiresAt => GetCached().RiotSessionExpiresAt;
    public string RiotId => GetCached().RiotId;
    public string RiotRegion => GetCached().RiotRegion;
    public string RiotPuuid => GetCached().RiotPuuid;
    public string PrimaryRole => GetCached().PrimaryRole;
    public bool OnboardingSkipped => GetCached().OnboardingSkipped;
    public bool AscentReminderDismissed => GetCached().AscentReminderDismissed;
    public bool SidebarAnimationEnabled => GetCached().SidebarAnimationEnabled;

    public bool IsAscentEnabled => AscentFolder is not null;

    public bool HasValidRiotSession =>
        !string.IsNullOrWhiteSpace(RiotSessionToken)
        && RiotSessionExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public bool RiotProxyEnabled =>
        HasValidRiotSession
        && !string.IsNullOrWhiteSpace(RiotId)
        && RiotId.Contains('#')
        && !string.IsNullOrWhiteSpace(RiotRegion);

    /// <summary>
    /// Skip onboarding if the user either (a) opted out entirely or
    /// (b) completed it — which now requires a primary role too.
    /// </summary>
    public bool OnboardingComplete =>
        OnboardingSkipped
        || (RiotProxyEnabled && !string.IsNullOrEmpty(PrimaryRole));

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

        var defaultDir = AppDataPaths.ClipsDirectory;
        Directory.CreateDirectory(defaultDir);
        return defaultDir;
    }
}

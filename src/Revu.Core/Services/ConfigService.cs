#nullable enable

using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Thread-safe config service that reads/writes %LOCALAPPDATA%\LoLReviewData\config.json.
/// Ported from Python config.py.
/// </summary>
public sealed class ConfigService : IConfigService
{
    private const string GithubTokenSecretName = "github_token";
    private const string RiotSessionTokenSecretName = "riot_session_token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly ILogger<ConfigService> _logger;
    private readonly IProtectedSecretStore _secrets;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _configDir;
    private readonly string _configFile;
    private AppConfig? _cached;

    public ConfigService(ILogger<ConfigService> logger, IProtectedSecretStore secrets)
        : this(logger, secrets, AppDataPaths.ConfigPath)
    {
    }

    public ConfigService(ILogger<ConfigService> logger, IProtectedSecretStore secrets, string configFile)
    {
        _logger = logger;
        _secrets = secrets;
        _configFile = configFile;
        _configDir = Path.GetDirectoryName(_configFile)!;

        TryCopyLegacyConfig();
    }

    // ── IConfigService convenience properties ───────────────────────

    public string GithubToken => GetCached().GithubToken;
    public string? AscentFolder => GetValidatedFolder(GetCached().AscentFolder);
    public string AscentFolderRaw => GetCached().AscentFolder;
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
    public bool MinimizeDuringGame => GetCached().MinimizeDuringGame;
    public bool AutoTimelineClippingEnabled => GetCached().AutoTimelineClippingEnabled;
    public bool AutoTimelineClippingHintDismissed => GetCached().AutoTimelineClippingHintDismissed;

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
            var persistable = PersistSecretsAndCreateSanitizedConfig(config);
            Directory.CreateDirectory(_configDir);
            var json = JsonSerializer.Serialize(persistable, JsonOptions);
            await WriteFileAtomicAsync(_configFile, json).ConfigureAwait(false);
            _cached = HydrateSecrets(persistable);
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

        // Synchronous fast-path: load from disk on first access.
        // Bounded wait so a stuck background writer can never freeze the UI thread.
        if (!_lock.Wait(TimeSpan.FromSeconds(2)))
        {
            // P-009: never fabricate an empty AppConfig here — a fabricated
            // config silently misconfigures every read that races a writer
            // (Ascent folder, Tilt Fix gate, clips folder, riot session).
            // Writes are atomic (temp + move), so an uncached disk read is
            // safe; the one-time secret-migration write stays lock-guarded.
            if (_cached is not null) return _cached;
            _logger.LogWarning("Config lock contended >2s; serving uncached disk read instead of empty defaults");
            return LoadFromDiskSync(allowMigrationWrite: false);
        }
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
            if (File.Exists(_configFile))
            {
                var json = await File.ReadAllTextAsync(_configFile).ConfigureAwait(false);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                if (!TryMigrateSecretsFromConfig(config, out var migrated))
                {
                    return config;
                }

                var sanitized = CreateSanitizedConfig(config);
                if (migrated)
                {
                    Directory.CreateDirectory(_configDir);
                    var sanitizedJson = JsonSerializer.Serialize(sanitized, JsonOptions);
                    await WriteFileAtomicAsync(_configFile, sanitizedJson).ConfigureAwait(false);
                }

                return HydrateSecrets(sanitized);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read config from {Path}", _configFile);
            BackupCorruptConfig();
        }
        return HydrateSecrets(new AppConfig());
    }

    private AppConfig LoadFromDiskSync(bool allowMigrationWrite = true)
    {
        try
        {
            if (File.Exists(_configFile))
            {
                var json = File.ReadAllText(_configFile);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                if (!TryMigrateSecretsFromConfig(config, out var migrated))
                {
                    return config;
                }

                var sanitized = CreateSanitizedConfig(config);
                if (migrated && allowMigrationWrite)
                {
                    Directory.CreateDirectory(_configDir);
                    var sanitizedJson = JsonSerializer.Serialize(sanitized, JsonOptions);
                    WriteFileAtomic(_configFile, sanitizedJson);
                }

                return HydrateSecrets(sanitized);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read config from {Path}", _configFile);
            BackupCorruptConfig();
        }
        return HydrateSecrets(new AppConfig());
    }

    // ── Atomic write + corruption recovery ──────────────────────────
    //
    // config.json must never be left truncated/partial: a crash or full disk
    // mid-write would otherwise zero the file, and the next load would silently
    // reset every setting to defaults. Write to a sibling temp file, flush, then
    // atomically replace the real file via File.Move(overwrite). Both files live
    // in the same directory so the rename stays on one volume (atomic on NTFS).

    private static async Task WriteFileAtomicAsync(string path, string contents)
    {
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, contents).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tmp);
            throw;
        }
    }

    private static void WriteFileAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tmp);
            throw;
        }
    }

    private static void TryDeleteTemp(string tmp)
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); }
        catch { /* best effort — a stray .tmp is harmless */ }
    }

    /// <summary>
    /// Preserve an unreadable/corrupt config.json so a parse failure doesn't
    /// silently discard the user's settings. Best-effort: a missing or
    /// already-gone file is fine, and any IO error here must not block the
    /// fall-back-to-defaults load path.
    /// </summary>
    private void BackupCorruptConfig()
    {
        try
        {
            if (!File.Exists(_configFile)) return;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = $"{_configFile}.corrupt-{stamp}";
            File.Copy(_configFile, dest, overwrite: true);
            _logger.LogWarning("Backed up unreadable config to {Path}", dest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not back up corrupt config from {Path}", _configFile);
        }
    }

    private void TryCopyLegacyConfig()
    {
        if (!string.Equals(_configFile, AppDataPaths.ConfigPath, StringComparison.OrdinalIgnoreCase)
            || File.Exists(_configFile))
        {
            return;
        }

        foreach (var legacyConfig in AppDataPaths.EnumerateLegacyConfigPaths())
        {
            if (!File.Exists(legacyConfig))
            {
                continue;
            }

            Directory.CreateDirectory(_configDir);
            File.Copy(legacyConfig, _configFile);
            break;
        }
    }

    private bool TryMigrateSecretsFromConfig(AppConfig config, out bool migrated)
    {
        migrated = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(config.GithubToken))
            {
                _secrets.SetSecret(GithubTokenSecretName, config.GithubToken);
                migrated = true;
            }

            if (!string.IsNullOrWhiteSpace(config.RiotSessionToken))
            {
                _secrets.SetSecret(RiotSessionTokenSecretName, config.RiotSessionToken);
                migrated = true;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not migrate config secrets into protected storage; leaving config in legacy shape.");
            migrated = false;
            return false;
        }
    }

    private AppConfig PersistSecretsAndCreateSanitizedConfig(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.GithubToken))
        {
            _secrets.ClearSecret(GithubTokenSecretName);
        }
        else
        {
            _secrets.SetSecret(GithubTokenSecretName, config.GithubToken);
        }

        if (string.IsNullOrWhiteSpace(config.RiotSessionToken))
        {
            _secrets.ClearSecret(RiotSessionTokenSecretName);
        }
        else
        {
            _secrets.SetSecret(RiotSessionTokenSecretName, config.RiotSessionToken);
        }

        return CreateSanitizedConfig(config);
    }

    private static AppConfig CreateSanitizedConfig(AppConfig config)
    {
        var copy = CloneConfig(config);
        copy.GithubToken = "";
        copy.RiotSessionToken = "";
        return copy;
    }

    private AppConfig HydrateSecrets(AppConfig config)
    {
        var copy = CloneConfig(config);
        copy.GithubToken = _secrets.GetSecret(GithubTokenSecretName) ?? "";
        copy.RiotSessionToken = _secrets.GetSecret(RiotSessionTokenSecretName) ?? "";
        return copy;
    }

    private static AppConfig CloneConfig(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
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

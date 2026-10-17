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
    // Retired: the GitHub-release updater's token. Velopack reads an anonymous feed, so
    // nothing consumes it; a legacy value is scrubbed from config.json and the store.
    private const string LegacyGithubTokenSecretName = "github_token";
    private const string RiotSessionTokenSecretName = "riot_session_token";
    // Login-unlock identity mirrored into DPAPI alongside the session token so a
    // config.json clobber (the P-020/P-023 empty-string-overwrite class) can't
    // silently log the user out: RiotProxyEnabled reads these store-first with the
    // plaintext config as fallback. NOT blanked from the sanitized config — the
    // settings/dashboard UI still reads the plaintext copy for display.
    private const string RiotIdSecretName = "riot_id";
    private const string RiotRegionSecretName = "riot_region";
    private const string RiotPuuidSecretName = "riot_puuid";
    // v3.2: the session EXPIRY mirrored into DPAPI next to the token. Observed
    // live 2026-07-10: config.json vanished from disk twice AFTER a logged
    // "session verified + persisted" (no error, no corrupt backup; the deleter
    // turned out to be BackupService.ResetAllDataAsync run from the Core test
    // suite against a temp DB — fixed 2026-09-09, and every write now keeps a
    // config.json.bak that LoadFromDisk restores from), and because the expiry used to live ONLY in plaintext
    // config.json, every such loss read as expiry=0 → signed out on the next
    // launch even though the token secret survived. Mirroring the expiry makes
    // the session recoverable from the store alone, closing the whole
    // "logged out every launch" class regardless of what eats the file.
    private const string RiotSessionExpiresAtSecretName = "riot_session_expires_at";

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
    public bool MinimizeDuringGame => GetCached().MinimizeDuringGame;
    public bool AutoTimelineClippingEnabled => GetCached().AutoTimelineClippingEnabled;
    public bool AutoTimelineClippingHintDismissed => GetCached().AutoTimelineClippingHintDismissed;
    public bool AutoClipObjectivesEnabled => GetCached().AutoClipObjectivesEnabled;


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

    // When true, the current SaveAsync is a DELIBERATE session clear (sign-out): the
    // empty-token / zero-expiry preservation guards are bypassed so the session is
    // actually wiped. Set only inside ClearSessionAsync, under the lock.
    private bool _clearingSession;

    public async Task SaveAsync(AppConfig config)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveUnlockedAsync(config).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Load, mutate and save under ONE lock hold, so two read-modify-write handlers can
    /// never lose each other's update (a sign-out undone by a Settings save, a resolved
    /// PUUID blanked by a stale copy). <paramref name="mutate"/> edits a fresh copy of
    /// the on-disk config and returns false to leave the file untouched. An existing but
    /// unreadable config.json throws here, so a writer can never persist defaults.
    /// </summary>
    public async Task<bool> UpdateAsync(Func<AppConfig, bool> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await LoadFromDiskAsync().ConfigureAwait(false);
            _cached = current;
            var working = CloneConfig(current);
            if (!mutate(working)) return false;
            await SaveUnlockedAsync(working).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveUnlockedAsync(AppConfig config)
    {
        // Expiry-clobber guard (P-020/P-023 class): RiotSessionExpiresAt lives only
        // in plaintext config.json (no DPAPI mirror). A read-modify-write handler
        // that didn't touch the session carries expiry=0; persisting that 0 would
        // log the user out even though the token is valid. Treat a 0/absent expiry
        // on a normal save as "leave the stored expiry unchanged" by carrying the
        // current on-disk value forward. A real sign-out goes through
        // ClearSessionAsync (which sets _clearingSession to bypass this).
        if (!_clearingSession && config.RiotSessionExpiresAt <= 0)
        {
            var priorExpiry = ReadPersistedExpiry();
            if (priorExpiry > 0) config.RiotSessionExpiresAt = priorExpiry;
        }

        var persistable = PersistSecretsAndCreateSanitizedConfig(config);
        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(persistable, JsonOptions);
        await WriteFileAtomicAsync(_configFile, json).ConfigureAwait(false);
        _cached = HydrateSecrets(persistable);
    }

    public async Task ClearSessionAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cfg = await LoadFromDiskAsync().ConfigureAwait(false);
            cfg.RiotSessionToken = "";
            cfg.RiotSessionEmail = "";
            cfg.RiotSessionExpiresAt = 0;
            // Force the wipe past the preservation guards in SaveAsync + the
            // ClearSecret in PersistSecretsAndCreateSanitizedConfig.
            _clearingSession = true;
            try
            {
                _secrets.ClearSecret(RiotSessionTokenSecretName);
                _secrets.ClearSecret(RiotSessionExpiresAtSecretName);
                var persistable = PersistSecretsAndCreateSanitizedConfig(cfg);
                Directory.CreateDirectory(_configDir);
                var json = JsonSerializer.Serialize(persistable, JsonOptions);
                await WriteFileAtomicAsync(_configFile, json).ConfigureAwait(false);
                _cached = HydrateSecrets(persistable);
            }
            finally
            {
                _clearingSession = false;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // Read just the persisted RiotSessionExpiresAt from config.json on disk, without
    // mutating _cached or taking the lock (callers already hold it). Falls back to
    // the DPAPI mirror when the file is missing/unreadable/absent-field, so a save
    // that lands while config.json is gone still writes the real expiry back out.
    private long ReadPersistedExpiry()
    {
        try
        {
            if (File.Exists(_configFile))
            {
                var json = File.ReadAllText(_configFile);
                var onDisk = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (onDisk is { RiotSessionExpiresAt: > 0 } cfg) return cfg.RiotSessionExpiresAt;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read persisted session expiry; falling back to the store.");
        }
        return long.TryParse(_secrets.GetSecret(RiotSessionExpiresAtSecretName), out var stored) && stored > 0
            ? stored
            : 0;
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
            // (Tilt Fix gate, clips folder, riot session).
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

    // H6: defaults are returned ONLY when config.json does not exist (first run, or a
    // loss with no .bak). A file that exists but cannot be read right now (antivirus,
    // a sync tool, another reader) is retried briefly and then THROWS: every writer is a
    // read-modify-write, so a fabricated default config would be saved over the real one.
    // A file that reads but is not valid JSON is kept as .corrupt-*, then the .bak is
    // tried, and only then defaults.
    private async Task<AppConfig> LoadFromDiskAsync()
    {
        var json = await ReadConfigTextAsync().ConfigureAwait(false);
        if (json is null) return HydrateSecrets(new AppConfig());

        var config = ParseOrRecover(json);
        if (!TryMigrateSecretsFromConfig(config, out var migrated))
        {
            return config;
        }

        var sanitized = CreateSanitizedConfig(config);
        if (migrated)
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                var sanitizedJson = JsonSerializer.Serialize(sanitized, JsonOptions);
                // keepBackup: false — the file being replaced still holds the
                // plaintext tokens this write exists to remove.
                await WriteFileAtomicAsync(_configFile, sanitizedJson, keepBackup: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The secrets are already in the store; the rewrite is retried next load.
                _logger.LogWarning(ex, "Could not rewrite sanitized config at {Path}", _configFile);
            }
        }

        return HydrateSecrets(sanitized);
    }

    private AppConfig LoadFromDiskSync(bool allowMigrationWrite = true)
    {
        var json = ReadConfigText();
        if (json is null) return HydrateSecrets(new AppConfig());

        var config = ParseOrRecover(json, mayWrite: allowMigrationWrite);
        if (!TryMigrateSecretsFromConfig(config, out var migrated))
        {
            return config;
        }

        var sanitized = CreateSanitizedConfig(config);
        if (migrated && allowMigrationWrite)
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                var sanitizedJson = JsonSerializer.Serialize(sanitized, JsonOptions);
                WriteFileAtomic(_configFile, sanitizedJson, keepBackup: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not rewrite sanitized config at {Path}", _configFile);
            }
        }

        return HydrateSecrets(sanitized);
    }

    // Short waits for a sharing violation (antivirus, backup or sync tools) to clear.
    private static readonly int[] ReadRetryDelaysMs = [50, 100, 200];

    /// <summary>config.json's text, or null when the file does not exist (after
    /// trying the .bak). Throws when it exists but stays unreadable.</summary>
    private async Task<string?> ReadConfigTextAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!ConfigFileExists()) return null;
            try
            {
                await using var stream = OpenConfigForRead(useAsync: true);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException)
                                       && attempt < ReadRetryDelaysMs.Length)
            {
                // Vanished between the check and the open: look again (and try the .bak).
            }
            catch (Exception ex) when (IsRetryableRead(ex, attempt))
            {
                _logger.LogDebug(ex, "config.json busy; retrying read");
                await Task.Delay(ReadRetryDelaysMs[attempt]).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Unreadable(ex);
            }
        }
    }

    private string? ReadConfigText()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!ConfigFileExists()) return null;
            try
            {
                using var stream = OpenConfigForRead(useAsync: false);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException)
                                       && attempt < ReadRetryDelaysMs.Length)
            {
                // Vanished between the check and the open: look again.
            }
            catch (Exception ex) when (IsRetryableRead(ex, attempt))
            {
                _logger.LogDebug(ex, "config.json busy; retrying read");
                Thread.Sleep(ReadRetryDelaysMs[attempt]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Unreadable(ex);
            }
        }
    }

    private bool ConfigFileExists()
    {
        if (File.Exists(_configFile)) return true;
        TryRestoreFromBackup();
        return File.Exists(_configFile);
    }

    // Shared read + delete access so an atomic replace (File.Move over this file)
    // from a writer never collides with this read handle.
    private FileStream OpenConfigForRead(bool useAsync) =>
        new(_configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, useAsync: useAsync);

    private static bool IsRetryableRead(Exception ex, int attempt) =>
        (ex is IOException or UnauthorizedAccessException)
        && ex is not (FileNotFoundException or DirectoryNotFoundException)
        && attempt < ReadRetryDelaysMs.Length;

    private ConfigUnreadableException Unreadable(Exception inner)
    {
        _logger.LogError(inner, "config.json at {Path} exists but cannot be read; refusing to use defaults", _configFile);
        return new ConfigUnreadableException("Your settings file could not be read right now. Nothing was changed; try again.", inner);
    }

    // mayWrite is false on GetCached's contended fallback, which reads without the lock:
    // there the .bak is only read, never copied over config.json, so it cannot race a
    // writer that holds the lock.
    private AppConfig ParseOrRecover(string json, bool mayWrite = true)
    {
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "config.json at {Path} is not valid JSON", _configFile);
            var kept = mayWrite && BackupCorruptConfig();
            if (TryReadBackupConfig(out var recovered))
            {
                // Put the last good copy back only once the corrupt one is preserved.
                if (kept)
                {
                    try
                    {
                        File.Copy(BackupPathFor(_configFile), _configFile, overwrite: true);
                        _logger.LogWarning("Restored config.json from its .bak after a corrupt read");
                    }
                    catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(copyEx, "Could not put config.json.bak back in place");
                    }
                }
                return recovered;
            }
            _logger.LogWarning("No usable config.json.bak; running on default settings until the next save");
            return new AppConfig();
        }
    }

    private bool TryReadBackupConfig(out AppConfig recovered)
    {
        recovered = new AppConfig();
        var bak = BackupPathFor(_configFile);
        try
        {
            if (!File.Exists(bak)) return false;
            var parsed = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(bak), JsonOptions);
            if (parsed is null) return false;
            recovered = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "config.json.bak is not usable either");
            return false;
        }
    }

    // ── Atomic write + corruption recovery ──────────────────────────
    //
    // config.json must never be left truncated/partial: a crash or full disk
    // mid-write would otherwise zero the file, and the next load would silently
    // reset every setting to defaults. Write to a sibling temp file, flush, then
    // atomically replace the real file via File.Move(overwrite). Both files live
    // in the same directory so the rename stays on one volume (atomic on NTFS).

    private static async Task WriteFileAtomicAsync(string path, string contents, bool keepBackup = true)
    {
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, contents).ConfigureAwait(false);
            if (keepBackup) KeepBackup(path); else DropBackup(path);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tmp);
            throw;
        }
    }

    private static void WriteFileAtomic(string path, string contents, bool keepBackup = true)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            if (keepBackup) KeepBackup(path); else DropBackup(path);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tmp);
            throw;
        }
    }

    // A rolling copy of the last good config.json, so a deleted file (anything
    // from a stray reset to a disk hiccup) costs nothing: LoadFromDisk puts it
    // back instead of silently running on defaults. Best effort — never blocks
    // the write.
    private static string BackupPathFor(string path) => path + ".bak";

    private static void KeepBackup(string path)
    {
        try { if (File.Exists(path)) File.Copy(path, BackupPathFor(path), overwrite: true); }
        catch { /* best effort */ }
    }

    // A write that must not be recoverable (the secret-migration rewrite): the
    // previous file carried plaintext tokens, so no copy of it may survive.
    private static void DropBackup(string path)
    {
        try { var bak = BackupPathFor(path); if (File.Exists(bak)) File.Delete(bak); }
        catch { /* best effort */ }
    }

    private bool _reportedMissing;

    private void TryRestoreFromBackup()
    {
        try
        {
            if (File.Exists(_configFile)) return;
            var bak = BackupPathFor(_configFile);
            if (File.Exists(bak))
            {
                File.Copy(bak, _configFile, overwrite: false);
                _logger.LogWarning("config.json was missing; restored it from {Backup}", bak);
                return;
            }
            // Missing with no backup and no way to tell first launch from loss
            // here — a session store beside it means this is NOT a fresh install.
            if (!_reportedMissing && File.Exists(Path.Combine(_configDir, "protected_secrets.bin")))
            {
                _reportedMissing = true;
                _logger.LogWarning(
                    "config.json is missing from {Dir} (secrets store present) — running on default settings until the next save",
                    _configDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Config backup restore check failed");
        }
    }

    private static void TryDeleteTemp(string tmp)
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); }
        catch { /* best effort — a stray .tmp is harmless */ }
    }

    /// <summary>
    /// Preserve a corrupt config.json so a parse failure doesn't silently discard the
    /// user's settings. Best-effort: returns false when no copy could be made, and the
    /// caller then leaves the corrupt file where it is.
    /// </summary>
    private bool BackupCorruptConfig()
    {
        try
        {
            if (!File.Exists(_configFile)) return false;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = $"{_configFile}.corrupt-{stamp}";
            File.Copy(_configFile, dest, overwrite: true);
            _logger.LogWarning("Backed up unreadable config to {Path}", dest);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not back up corrupt config from {Path}", _configFile);
            return false;
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
            // A legacy plaintext GitHub token is not kept anywhere: the sanitized
            // rewrite drops it from config.json, and a stored copy is removed once.
            ClearLegacyGithubToken();
            if (!string.IsNullOrWhiteSpace(config.GithubToken))
            {
                migrated = true;
            }

            if (!string.IsNullOrWhiteSpace(config.RiotSessionToken))
            {
                _secrets.SetSecret(RiotSessionTokenSecretName, config.RiotSessionToken);
                migrated = true;
            }

            // Seed the DPAPI identity mirror from a legacy plaintext config so the
            // self-heal works on the very first load after upgrade (P1/19-02).
            if (!string.IsNullOrWhiteSpace(config.RiotId))
            {
                _secrets.SetSecret(RiotIdSecretName, config.RiotId);
                migrated = true;
            }
            if (!string.IsNullOrWhiteSpace(config.RiotRegion))
            {
                _secrets.SetSecret(RiotRegionSecretName, config.RiotRegion);
                migrated = true;
            }
            if (!string.IsNullOrWhiteSpace(config.RiotPuuid))
            {
                _secrets.SetSecret(RiotPuuidSecretName, config.RiotPuuid);
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
        ClearLegacyGithubToken();

        // Session token: an empty token on a NORMAL save means "this writer didn't
        // touch the session — leave the stored token alone", NOT "clear it". Only the
        // sole writer that legitimately owns the session (verify, which sets a real
        // token) updates it; every other read-modify-write handler (resolve,
        // config/save, dismiss-flag writers) carries an in-memory config whose
        // RiotSessionToken is "" unless HydrateSecrets happened to repopulate it, and
        // must NEVER ClearSecret it. ClearSecret is reserved for an explicit sign-out
        // (ClearSessionAsync). This is the destructive-clobber fix: previously ANY save
        // with an empty token deleted the token from DPAPI, logging the user out and
        // making clip-share fail. (Mirrors the P-020/P-023 "empty = leave unchanged"
        // guard already applied to riot_id/region/folder fields.)
        if (!string.IsNullOrWhiteSpace(config.RiotSessionToken))
        {
            _secrets.SetSecret(RiotSessionTokenSecretName, config.RiotSessionToken);
            // Mirror the expiry alongside the token (see the secret-name note):
            // only a positive expiry writes — a token-carrying save with expiry 0
            // (hydrated read-modify-write) must not stomp the stored value.
            if (config.RiotSessionExpiresAt > 0)
                _secrets.SetSecret(RiotSessionExpiresAtSecretName, config.RiotSessionExpiresAt.ToString());
        }

        // Mirror the login-unlock identity into DPAPI next to the token (P1/19-02).
        // This is a MIRROR, not a move: the plaintext copy stays for UI display
        // (CreateSanitizedConfig does not blank these), but the protected copy lets
        // RiotProxyEnabled survive a config.json clobber.
        SetOrClearSecret(RiotIdSecretName, config.RiotId);
        SetOrClearSecret(RiotRegionSecretName, config.RiotRegion);
        SetOrClearSecret(RiotPuuidSecretName, config.RiotPuuid);

        return CreateSanitizedConfig(config);
    }

    private bool _legacyGithubTokenCleared;

    // Once per instance: drop the retired GitHub token from the protected store.
    // Housekeeping only, so a failure is logged and retried on the next save.
    private void ClearLegacyGithubToken()
    {
        if (_legacyGithubTokenCleared) return;
        try
        {
            _secrets.ClearSecret(LegacyGithubTokenSecretName);
            _legacyGithubTokenCleared = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove the retired GitHub token from protected storage");
        }
    }

    private void SetOrClearSecret(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            _secrets.ClearSecret(name);
        else
            _secrets.SetSecret(name, value);
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
        copy.GithubToken = "";
        copy.RiotSessionToken = _secrets.GetSecret(RiotSessionTokenSecretName) ?? "";
        // Expiry: plaintext wins when present (written together with the mirror);
        // a missing/clobbered config.json (expiry 0) recovers from the store so
        // the surviving token secret still counts as a live session.
        if (copy.RiotSessionExpiresAt <= 0
            && long.TryParse(_secrets.GetSecret(RiotSessionExpiresAtSecretName), out var storedExpiry)
            && storedExpiry > 0)
        {
            copy.RiotSessionExpiresAt = storedExpiry;
        }
        // Identity: store-first, plaintext-fallback. If config.json was clobbered
        // (riot_id/region/puuid blanked) but the DPAPI mirror survives, recover from
        // the store so the user stays logged in (P1/19-02). The plaintext value wins
        // only when the store is empty (e.g. legacy config predating the mirror).
        copy.RiotId = PreferSecret(RiotIdSecretName, copy.RiotId);
        copy.RiotRegion = PreferSecret(RiotRegionSecretName, copy.RiotRegion);
        copy.RiotPuuid = PreferSecret(RiotPuuidSecretName, copy.RiotPuuid);
        return copy;
    }

    private string PreferSecret(string name, string plaintext)
    {
        var stored = _secrets.GetSecret(name);
        return !string.IsNullOrWhiteSpace(stored) ? stored : (plaintext ?? "");
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

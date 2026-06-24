using Xunit;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Contract coverage for the sidecar's <c>POST /api/config/save</c> seam, exercised
/// through Revu.Core types only (the harness deliberately does NOT reference
/// Revu.Sidecar, and <c>SaveConfigBody</c> is internal there).
///
/// The sidecar handler is a read-modify-write loop:
///   var cfg = await Config.LoadAsync();
///   if (ConfigSaveGuards.TryResolveFolderWrite(body.AscentFolder, out var a)) cfg.AscentFolder = a;
///   ... (clips/backup folders, riot id/region via TryResolveTextWrite) ...
///   await Config.SaveAsync(cfg);
/// so the contract under test is the COMPOSITION of (1) the ConfigSaveGuards empty-as-
/// unchanged guard the endpoint applies and (2) the real ConfigService round-trip
/// (atomic file write + DPAPI secret mirror/hydrate). <see cref="ApplySaveConfig"/>
/// reproduces the handler's mapping byte-for-byte (Program.cs ~L1012-1037).
///
/// Two themes:
///  (A) P-020 / P-023 empty-string-overwrite class — a Save with blank folder / riot
///      fields must NOT blank a configured value.
///  (B) the v3.1.1 DPAPI riot-identity mirror — riot_id/region/puuid are mirrored into
///      the protected store on Save AND self-heal a clobbered config.json on Load, so
///      RiotProxyEnabled survives the P-020 clobber.
/// </summary>
public sealed class ConfigSaveContractTests
{
    private const long FarFutureExpiry = 4102444800; // 2100-01-01, well past "now".

    // ── (A) P-020 / P-023 empty-string-overwrite guards ───────────────────────

    [Fact]
    public async Task SaveConfig_EmptyFolderField_DoesNotBlankConfiguredFolders()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        // First save: user configured all three folders.
        await ApplySaveConfig(
            service,
            ascentFolder: @"C:\Users\me\Videos\Ascent",
            clipsFolder: @"C:\Users\me\Videos\Clips",
            backupFolder: @"C:\Users\me\Backups");

        // Second save: an unhydrated Settings page sends empty folder fields while
        // an unrelated toggle changes. This is the exact P-023 trigger.
        await ApplySaveConfig(
            service,
            ascentFolder: "",
            clipsFolder: "",
            backupFolder: "",
            tiltFixMode: true);

        var reloaded = await service.LoadAsync();
        Assert.Equal(@"C:\Users\me\Videos\Ascent", reloaded.AscentFolder);
        Assert.Equal(@"C:\Users\me\Videos\Clips", reloaded.ClipsFolder);
        Assert.Equal(@"C:\Users\me\Backups", reloaded.BackupFolder);
        Assert.True(reloaded.TiltFixMode); // the field that actually changed DID save
    }

    [Fact]
    public async Task SaveConfig_FolderClearSentinel_ExplicitlyBlanksOnlyThatFolder()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await ApplySaveConfig(
            service,
            ascentFolder: @"C:\Users\me\Videos\Ascent",
            clipsFolder: @"C:\Users\me\Videos\Clips",
            backupFolder: @"C:\Users\me\Backups");

        // User pressed Clear on Ascent only; clips/backup inputs were empty (unchanged).
        await ApplySaveConfig(
            service,
            ascentFolder: ConfigSaveGuards.FolderClearSentinel,
            clipsFolder: "",
            backupFolder: "");

        var reloaded = await service.LoadAsync();
        Assert.Equal("", reloaded.AscentFolder); // explicitly cleared
        Assert.Equal(@"C:\Users\me\Videos\Clips", reloaded.ClipsFolder); // untouched
        Assert.Equal(@"C:\Users\me\Backups", reloaded.BackupFolder); // untouched
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveConfig_EmptyRiotIdAndRegion_DoNotBlankConfiguredAccount(string? blank)
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        // Establish a logged-in account with a valid session.
        await ApplySaveConfig(
            service,
            riotId: "chapy#na1",
            region: "NA1",
            riotPuuid: "puuid-abc",
            riotSessionToken: "session-secret",
            riotSessionExpiresAt: FarFutureExpiry);

        // An unhydrated Settings save sends blank riot id/region (the <select> default).
        await ApplySaveConfig(service, riotId: blank, region: blank);

        var reloaded = await service.LoadAsync();
        Assert.Equal("chapy#na1", reloaded.RiotId);
        Assert.Equal("na1", reloaded.RiotRegion);
        Assert.True(service.RiotProxyEnabled);
    }

    [Fact]
    public async Task SaveConfig_RealRegion_IsLowerCasedLikeTheHandler()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await ApplySaveConfig(service, riotId: "chapy#euw", region: "EUW1");

        var reloaded = await service.LoadAsync();
        Assert.Equal("euw1", reloaded.RiotRegion); // handler ToLowerInvariant()s it
    }

    // ── (B) v3.1.1 DPAPI riot-identity mirror (sidecar-contract angle) ─────────

    [Fact]
    public async Task SaveConfig_MirrorsRiotIdentityIntoProtectedStore()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await ApplySaveConfig(
            service,
            riotId: "chapy#na1",
            region: "na1",
            riotPuuid: "puuid-abc",
            riotSessionToken: "session-secret",
            riotSessionExpiresAt: FarFutureExpiry);

        // The identity is mirrored into DPAPI alongside the session token.
        Assert.Equal("chapy#na1", secrets.GetSecret("riot_id"));
        Assert.Equal("na1", secrets.GetSecret("riot_region"));
        Assert.Equal("puuid-abc", secrets.GetSecret("riot_puuid"));
        // Session token IS sanitized out of plaintext (moved to the store).
        Assert.Equal("session-secret", secrets.GetSecret("riot_session_token"));

        // ...but the plaintext identity copies remain for UI display (a MIRROR, not a move).
        var onDisk = await File.ReadAllTextAsync(scope.ConfigPath);
        Assert.Contains("chapy#na1", onDisk);
        Assert.DoesNotContain("session-secret", onDisk); // token still sanitized
    }

    [Fact]
    public async Task SaveConfig_ThenClobberedConfig_SelfHealsIdentityAndKeepsProxyEnabled()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();

        // First service instance saves a logged-in account; this mirrors the
        // identity + session token into the (shared) protected store.
        var saver = CreateService(scope.ConfigPath, secrets);
        await ApplySaveConfig(
            saver,
            riotId: "chapy#na1",
            region: "na1",
            riotPuuid: "puuid-abc",
            riotSessionToken: "session-secret",
            riotSessionExpiresAt: FarFutureExpiry);

        // Simulate the P-020 clobber landing on disk AFTER the store was seeded:
        // riot_id/region/puuid blanked in config.json, but the session expiry stays.
        var clobbered = new AppConfig
        {
            RiotSessionExpiresAt = FarFutureExpiry,
            RiotId = "",
            RiotRegion = "",
            RiotPuuid = "",
        };
        await File.WriteAllTextAsync(
            scope.ConfigPath,
            System.Text.Json.JsonSerializer.Serialize(clobbered, SnakeCaseJson));

        // A fresh service (new launch) reading the clobbered file + surviving store.
        var reader = CreateService(scope.ConfigPath, secrets);
        var loaded = await reader.LoadAsync();

        // Identity self-heals from the store; the login gate stays satisfied.
        Assert.Equal("chapy#na1", loaded.RiotId);
        Assert.Equal("na1", loaded.RiotRegion);
        Assert.Equal("puuid-abc", loaded.RiotPuuid);
        Assert.Equal("session-secret", loaded.RiotSessionToken);
        Assert.True(reader.HasValidRiotSession);
        Assert.True(reader.RiotProxyEnabled);
    }

    [Fact]
    public async Task SaveConfig_LegacyPlaintextOnly_SeedsStoreAndKeepsProxyEnabled()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();

        // Legacy config predating the mirror: identity + token only in plaintext.
        var legacy = new AppConfig
        {
            RiotSessionToken = "session-secret",
            RiotSessionExpiresAt = FarFutureExpiry,
            RiotId = "legacy#euw",
            RiotRegion = "euw1",
            RiotPuuid = "legacy-puuid",
        };
        await File.WriteAllTextAsync(
            scope.ConfigPath,
            System.Text.Json.JsonSerializer.Serialize(legacy, SnakeCaseJson));

        var service = CreateService(scope.ConfigPath, secrets);
        var loaded = await service.LoadAsync();

        // Plaintext is used when the store is empty, AND the load seeds the store
        // so the next clobber can self-heal.
        Assert.Equal("legacy#euw", loaded.RiotId);
        Assert.Equal("euw1", loaded.RiotRegion);
        Assert.Equal("legacy-puuid", loaded.RiotPuuid);
        Assert.Equal("legacy#euw", secrets.GetSecret("riot_id"));
        Assert.Equal("legacy-puuid", secrets.GetSecret("riot_puuid"));
        Assert.True(service.RiotProxyEnabled);
    }

    [Fact]
    public async Task SaveConfig_DoesNotClobberUnrelatedKeysOnPartialSave()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        // Full first save.
        await ApplySaveConfig(
            service,
            ascentFolder: @"C:\Ascent",
            clipsFolder: @"C:\Clips",
            riotId: "chapy#na1",
            region: "na1",
            primaryRole: "MIDDLE",
            tiltFixMode: true);

        // A partial save touching ONLY clips-max-size leaves everything else intact
        // (the read-modify-write contract: null = leave unchanged).
        await ApplySaveConfig(service, clipsMaxSizeMb: 4096);

        var reloaded = await service.LoadAsync();
        Assert.Equal(@"C:\Ascent", reloaded.AscentFolder);
        Assert.Equal(@"C:\Clips", reloaded.ClipsFolder);
        Assert.Equal("chapy#na1", reloaded.RiotId);
        Assert.Equal("na1", reloaded.RiotRegion);
        Assert.Equal("MIDDLE", reloaded.PrimaryRole);
        Assert.True(reloaded.TiltFixMode);
        Assert.Equal(4096, reloaded.ClipsMaxSizeMb); // the one field that changed
    }

    [Fact]
    public async Task SaveConfig_TutorialProgress_RoundTripsAndSurvivesPartialSave()
    {
        using var scope = new TempConfigScope();
        var secrets = new FakeProtectedSecretStore();
        var service = CreateService(scope.ConfigPath, secrets);

        await ApplySaveConfig(
            service,
            firstReviewTutorialStep: "vod",
            firstReviewTutorialCompleted: false,
            firstReviewTutorialDismissed: false,
            firstReviewTutorialObjectiveId: 123,
            firstReviewTutorialGameId: 456);

        await ApplySaveConfig(service, clipsMaxSizeMb: 4096);

        var reloaded = await service.LoadAsync();
        Assert.Equal("vod", reloaded.FirstReviewTutorialStep);
        Assert.False(reloaded.FirstReviewTutorialCompleted);
        Assert.False(reloaded.FirstReviewTutorialDismissed);
        Assert.Equal(123, reloaded.FirstReviewTutorialObjectiveId);
        Assert.Equal(456, reloaded.FirstReviewTutorialGameId);
        Assert.Equal(4096, reloaded.ClipsMaxSizeMb);
    }

    // ── Test infrastructure ───────────────────────────────────────────────────

    private static readonly System.Text.Json.JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static ConfigService CreateService(string configPath, IProtectedSecretStore secrets) =>
        new(NullLogger<ConfigService>.Instance, secrets, configPath);

    /// <summary>
    /// Reproduces the sidecar's <c>POST /api/config/save</c> read-modify-write loop
    /// (Program.cs ~L1012-1037) using the SAME ConfigSaveGuards calls the real handler
    /// uses. Every parameter is nullable: null = "leave unchanged", matching the
    /// SaveConfigBody contract. Non-guarded fields (riot session token/expiry, primary
    /// role, max size) follow the handler's "null = unchanged" rule directly.
    /// </summary>
    private static async Task ApplySaveConfig(
        ConfigService service,
        string? ascentFolder = null,
        string? clipsFolder = null,
        string? backupFolder = null,
        int? clipsMaxSizeMb = null,
        bool? tiltFixMode = null,
        string? riotId = null,
        string? region = null,
        string? riotPuuid = null,
        string? riotSessionToken = null,
        long? riotSessionExpiresAt = null,
        string? primaryRole = null,
        string? firstReviewTutorialStep = null,
        bool? firstReviewTutorialCompleted = null,
        bool? firstReviewTutorialDismissed = null,
        long? firstReviewTutorialObjectiveId = null,
        long? firstReviewTutorialGameId = null)
    {
        var cfg = await service.LoadAsync();

        // Folder fields: null/empty = unchanged; sentinel = explicit clear; else set.
        if (ConfigSaveGuards.TryResolveFolderWrite(ascentFolder, out var ascent)) cfg.AscentFolder = ascent;
        if (ConfigSaveGuards.TryResolveFolderWrite(clipsFolder, out var clips)) cfg.ClipsFolder = clips;
        if (ConfigSaveGuards.TryResolveFolderWrite(backupFolder, out var backup)) cfg.BackupFolder = backup;

        if (clipsMaxSizeMb is not null) cfg.ClipsMaxSizeMb = clipsMaxSizeMb.Value;
        if (tiltFixMode is not null) cfg.TiltFixMode = tiltFixMode.Value;

        // Riot id/region: null OR empty = leave unchanged (P-020 clobber guard).
        if (ConfigSaveGuards.TryResolveTextWrite(riotId, out var rid)) cfg.RiotId = rid;
        if (ConfigSaveGuards.TryResolveTextWrite(region, out var reg)) cfg.RiotRegion = reg.ToLowerInvariant();

        // The remaining fields are not guarded by the empty-as-unchanged rule in the
        // handler; they participate in the round-trip / mirror directly. We only mutate
        // when a value is supplied so a partial save stays partial.
        if (riotPuuid is not null) cfg.RiotPuuid = riotPuuid;
        if (riotSessionToken is not null) cfg.RiotSessionToken = riotSessionToken;
        if (riotSessionExpiresAt is not null) cfg.RiotSessionExpiresAt = riotSessionExpiresAt.Value;
        if (primaryRole is not null) cfg.PrimaryRole = primaryRole;
        if (firstReviewTutorialStep is not null) cfg.FirstReviewTutorialStep = firstReviewTutorialStep.Trim();
        if (firstReviewTutorialCompleted is not null) cfg.FirstReviewTutorialCompleted = firstReviewTutorialCompleted.Value;
        if (firstReviewTutorialDismissed is not null) cfg.FirstReviewTutorialDismissed = firstReviewTutorialDismissed.Value;
        if (firstReviewTutorialObjectiveId is not null) cfg.FirstReviewTutorialObjectiveId = Math.Max(0, firstReviewTutorialObjectiveId.Value);
        if (firstReviewTutorialGameId is not null) cfg.FirstReviewTutorialGameId = Math.Max(0, firstReviewTutorialGameId.Value);

        await service.SaveAsync(cfg);
    }

    private sealed class TempConfigScope : IDisposable
    {
        private readonly string _root;

        public TempConfigScope()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "Revu.Sidecar.Tests.Config",
                Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// In-memory <see cref="IProtectedSecretStore"/> mirroring the fake in
    /// Revu.Core.Tests/ConfigServiceProtectedSecretsTests.cs — stands in for DPAPI.
    /// </summary>
    private sealed class FakeProtectedSecretStore : IProtectedSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? GetSecret(string name) => _values.TryGetValue(name, out var value) ? value : null;

        public void SetSecret(string name, string value) => _values[name] = value;

        public void ClearSecret(string name) => _values.Remove(name);
    }
}

#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// GET /api/config — read the app config the Settings page (and several other
// pages) edit. SHARED endpoint: Dashboard greeting (RiotId), the Ascent VOD
// reminder (IsAscentEnabled / AscentReminderDismissed), and the VOD viewer's
// auto-clipping hint (AutoTimelineClippingHintDismissed) all read config too.
//
// Same conventions as the other snapshot builders: PascalCase here, camelCase on
// the wire (Program.cs serializer). NO brushes — config carries no colors.
//
// We read via IConfigService.LoadAsync() (a DISK re-read), not the cached
// convenience properties: the sidecar's read-graph IConfigService is a separate
// singleton/cache from the write-graph one, so forcing a disk read here makes
// GET reflect whatever POST /api/config/save just persisted. The folder fields
// are returned RAW (config.AscentFolder/ClipsFolder/BackupFolder), not the
// validated convenience-property variants, because the editor must round-trip
// exactly what the user typed even if the folder is currently missing.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ConfigSnapshotBuilder
{
    private readonly IConfigService _config;
    private readonly ILogger<ConfigSnapshotBuilder> _logger;

    public ConfigSnapshotBuilder(IConfigService config, ILogger<ConfigSnapshotBuilder> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<ConfigDto> BuildAsync(CancellationToken ct = default)
    {
        AppConfig cfg;
        try
        {
            cfg = await _config.LoadAsync();
        }
        catch (Exception ex)
        {
            // Mirror the WinUI page's LoadAsync try/catch: degrade to defaults so
            // the Settings page still renders rather than blanking.
            _logger.LogWarning(ex, "Config: LoadAsync failed; serving defaults");
            cfg = new AppConfig();
        }

        // RiotAuthState mirrors the WinUI RestoreRiotAuthState: loggedIn only when
        // the token is non-empty AND unexpired; otherwise loggedOut. (codeSent is
        // a transient client-only state the Settings page owns; not derivable from
        // persisted config, so GET only ever reports loggedIn / loggedOut.)
        var loggedIn = _config.HasValidRiotSession;

        return new ConfigDto(
            AscentFolder: cfg.AscentFolder ?? "",
            AscentReminderDismissed: cfg.AscentReminderDismissed,
            IsAscentEnabled: _config.IsAscentEnabled,
            ClipsFolder: cfg.ClipsFolder ?? "",
            ClipsMaxSizeMb: cfg.ClipsMaxSizeMb,
            BackupEnabled: cfg.BackupEnabled,
            BackupFolder: cfg.BackupFolder ?? "",
            TiltFixMode: cfg.TiltFixMode,
            RequireReviewNotes: cfg.RequireReviewNotes,
            SidebarAnimationEnabled: cfg.SidebarAnimationEnabled,
            MinimizeDuringGame: cfg.MinimizeDuringGame,
            AutoTimelineClippingEnabled: cfg.AutoTimelineClippingEnabled,
            AutoTimelineClippingHintDismissed: cfg.AutoTimelineClippingHintDismissed,
            RiotId: cfg.RiotId ?? "",
            Region: cfg.RiotRegion ?? "",
            PrimaryRole: cfg.PrimaryRole ?? "",
            RiotSessionEmail: cfg.RiotSessionEmail ?? "",
            RiotAuthState: loggedIn ? "loggedIn" : "loggedOut");
    }
}

/// <summary>
/// Editable + derived app-config surface returned by GET /api/config. Field set
/// matches the WinUI SettingsViewModel save list plus the cross-page reads the
/// Batch-0 spec calls out (AscentReminderDismissed, IsAscentEnabled,
/// AutoTimelineClippingHintDismissed). RiotSessionToken/Puuid are deliberately
/// NOT exposed — secrets stay server-side.
/// </summary>
public sealed record ConfigDto(
    string AscentFolder,
    bool AscentReminderDismissed,
    // Derived: true when AscentFolder validates to a real directory.
    bool IsAscentEnabled,
    string ClipsFolder,
    int ClipsMaxSizeMb,
    bool BackupEnabled,
    string BackupFolder,
    // NOTE name mismatch (mirror WinUI): config.TiltFixMode <- VM TiltFixEnabled.
    bool TiltFixMode,
    bool RequireReviewNotes,
    bool SidebarAnimationEnabled,
    bool MinimizeDuringGame,
    bool AutoTimelineClippingEnabled,
    bool AutoTimelineClippingHintDismissed,
    string RiotId,
    // config.RiotRegion, surfaced as "region" on the wire (Batch-0 spec name).
    string Region,
    string PrimaryRole,
    // Read-only: which email is signed in (display only; "" when logged out).
    string RiotSessionEmail,
    // "loggedIn" | "loggedOut" — drives which account sub-panel the page shows.
    string RiotAuthState);

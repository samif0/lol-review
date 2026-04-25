#nullable enable

using System;
using System.IO;
using Microsoft.Win32;
using Revu.Core.Data;

namespace Revu.App.Helpers;

/// <summary>
/// v2.16: belt-and-suspenders for the Windows Add/Remove Programs entry.
///
/// Velopack normally registers an uninstall key under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\&lt;packId&gt;</c>
/// when Setup.exe runs. Some users ended up without it (older installer
/// variant, registry wipe, manual install). On every app launch we check
/// whether the key exists and write it ourselves if missing, pointing at
/// Velopack's standard <c>Update.exe --uninstall</c> entry point.
///
/// All operations are best-effort and silent — a missing/locked registry
/// must never block app startup.
/// </summary>
internal static class UninstallEntryRegistrar
{
    private const string PackId = "LoLReview";
    private const string DisplayName = "Revu";
    private const string Publisher = "Sami Fawcett";
    private const string UninstallKeyRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static void EnsureRegistered(string appVersion)
    {
        try
        {
            var subKeyPath = $@"{UninstallKeyRoot}\{PackId}";
            using var existing = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            if (existing is not null)
            {
                // Velopack (or a prior run of this helper) already wrote the
                // key. Don't clobber — Velopack updates it during install.
                return;
            }

            var installRoot = AppDataPaths.InstallRoot;
            var updateExe = Path.Combine(installRoot, "Update.exe");
            if (!File.Exists(updateExe))
            {
                // Not running from a Velopack install (likely a dev build).
                // Add/Remove Programs only makes sense for installed copies.
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
            if (key is null) return;

            key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", appVersion, RegistryValueKind.String);
            key.SetValue("Publisher", Publisher, RegistryValueKind.String);
            key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{updateExe}\" --uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{updateExe}\" --uninstall --silent", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            // Best-effort icon — Velopack drops the main exe under current/
            // after first launch.
            var iconCandidate = Path.Combine(installRoot, "current", "LoLReview.App.exe");
            if (File.Exists(iconCandidate))
            {
                key.SetValue("DisplayIcon", iconCandidate, RegistryValueKind.String);
            }

            AppDiagnostics.WriteVerbose("startup.log", $"Uninstall registry entry registered at HKCU\\{subKeyPath}");
        }
        catch (Exception ex)
        {
            // Never fatal — Add/Remove Programs missing is annoying, an app
            // crash on launch is much worse.
            AppDiagnostics.WriteVerbose("startup.log", $"UninstallEntryRegistrar failed: {ex.Message}");
        }
    }
}

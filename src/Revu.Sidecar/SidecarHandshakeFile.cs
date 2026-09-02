#nullable enable

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Revu.Sidecar;

/// <summary>
/// Publishes the <c>{port, token}</c> handshake the Tauri host reads to reach
/// the sidecar (<c>sidecar.rs</c> — <c>Handshake { port: u16, token: String }</c>).
/// The token is the per-launch bearer that gates every endpoint except
/// <c>/api/health</c>, so the file is locked down to the current user before the
/// token lands in it: no window where the payload sits under the directory's
/// inherited ACL.
/// </summary>
internal static class SidecarHandshakeFile
{
    internal const string FileName = "sidecar.json";

    /// <summary>
    /// Writes <c>sidecar.json</c> under <paramref name="directory"/> (created if
    /// missing) and returns its full path. On Windows the file carries an
    /// explicit, non-inheriting DACL granting FullControl to the current user
    /// only; elsewhere it is mode 0600.
    /// </summary>
    internal static string Write(string directory, int port, string token)
    {
        Directory.CreateDirectory(directory); // sidecar handshake dir — NOT the DB data dir.

        var file = Path.Combine(directory, FileName);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { port, token });

        if (OperatingSystem.IsWindows())
            WriteWindows(file, payload);
        else
            WriteUnix(file, payload);

        return file;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string file, byte[] payload)
    {
        var me = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity has no SID; cannot lock down the sidecar handshake.");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));

        // FileSystemAclExtensions.Create hands the descriptor to CreateFile, so a
        // NEW file is born with the restricted DACL. CreateFile ignores the
        // descriptor when it merely truncates an existing file (a handshake left
        // by an earlier launch or an older build), so never truncate: remove any
        // stale file first and create the handshake fresh (CreateNew) so the
        // descriptor always applies before any byte of the payload is written.
        // The Tauri host already deletes the stale file before spawning us.
        if (File.Exists(file))
            File.Delete(file);

        // READ_CONTROL (ReadPermissions) + WRITE_DAC (ChangePermissions) on the
        // handle so the DACL can be read back and, if ever needed, re-applied:
        // SetSecurityInfo on a protected DACL needs both, not just WRITE_DAC.
        using var stream = new FileInfo(file).Create(
            FileMode.CreateNew,
            FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            security);

        // Belt and braces: confirm the descriptor took (protected, single rule for
        // the current user). Only if it did not — e.g. an unexpected inheritance
        // merge — re-apply it on the handle before the payload lands.
        if (!IsLockedDownTo(stream.GetAccessControl(), me))
            stream.SetAccessControl(security);

        stream.Write(payload, 0, payload.Length);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsLockedDownTo(FileSecurity applied, SecurityIdentifier me)
    {
        if (!applied.AreAccessRulesProtected) return false;
        var rules = applied.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        if (rules.Count != 1) return false;
        var rule = (FileSystemAccessRule)rules[0]!;
        return !rule.IsInherited
            && rule.AccessControlType == AccessControlType.Allow
            && rule.IdentityReference == me;
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteUnix(string file, byte[] payload)
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = ownerOnly, // applied at open(2) for a new file
        });
        // Only creation honours UnixCreateMode; a truncated pre-existing file keeps
        // its old mode, so tighten it on the handle before the payload lands.
        File.SetUnixFileMode(stream.SafeFileHandle, ownerOnly);
        stream.Write(payload, 0, payload.Length);
    }
}

#nullable enable
using System.Security.AccessControl;
using System.Security.Principal;

namespace Revu.Sidecar;

/// <summary>
/// Creates the sidecar handshake file locked down to the current user. The
/// handshake carries the per-launch bearer token that gates every sidecar
/// endpoint, so it must never sit under the directory's inherited ACL
/// (Administrators, SYSTEM, or whatever a REVU_DATA_ROOT override grants).
/// </summary>
internal static class SidecarHandshakeFile
{
    /// <summary>
    /// Creates <paramref name="path"/> as a new file whose explicit, protected
    /// DACL grants FullControl to the current user only, then writes
    /// <paramref name="payload"/>. The descriptor is handed to CreateFile, so it
    /// applies before any byte lands, and a same-volume rename keeps it, which is
    /// how <see cref="SidecarHostSession.Publish"/> swaps the file into place.
    /// </summary>
    internal static void CreateOwnerOnly(string path, byte[] payload)
    {
        var me = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity has no SID; cannot lock down the sidecar handshake.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));

        // CreateFile ignores the descriptor when it truncates an existing file, so
        // never reuse one: a leftover (e.g. from a crashed write) is removed and
        // the file is always born fresh with the restricted DACL.
        if (File.Exists(path)) File.Delete(path);
        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write,
            FileShare.None, bufferSize: 4096, FileOptions.None, security);
        stream.Write(payload, 0, payload.Length);
    }
}

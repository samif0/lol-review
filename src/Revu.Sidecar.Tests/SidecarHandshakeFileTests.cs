using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The sidecar handshake file (<c>%LOCALAPPDATA%\Revu\sidecar.json</c>) carries
/// the per-launch bearer token that gates every sidecar endpoint. These pin two
/// contracts: the payload shape the Tauri host's <c>Handshake</c> struct
/// deserialises (<c>port</c> number, <c>token</c> string), and the lock-down —
/// the file must be readable by the current user only, from the moment it exists.
///
/// <para>
/// The ACL assertions are Windows-only (they run on the windows-latest CI leg and
/// early-return elsewhere); the unix-mode assertions are the non-Windows mirror.
/// </para>
/// </summary>
public sealed class SidecarHandshakeFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "revu-handshake-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Write_ProducesPortAndTokenJson_InTheShapeTheHostExpects()
    {
        var path = SidecarHandshakeFile.Write(_dir, 51234, "abc123token");

        Assert.Equal(Path.Combine(_dir, "sidecar.json"), path);
        Assert.True(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        // Exactly the two fields the Rust Handshake struct declares, typed the way
        // serde will read them: port as a JSON number (u16), token as a JSON string.
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("port").ValueKind);
        Assert.Equal(51234, root.GetProperty("port").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("token").ValueKind);
        Assert.Equal("abc123token", root.GetProperty("token").GetString());
    }

    [Fact]
    public void Write_Overwrites_AnExistingHandshake()
    {
        SidecarHandshakeFile.Write(_dir, 1111, "first");
        var path = SidecarHandshakeFile.Write(_dir, 2222, "second");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2222, doc.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("second", doc.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void Write_OnWindows_RestrictsTheDaclToTheCurrentUserOnly()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows ACLs — asserted on the Windows CI leg.

        var path = SidecarHandshakeFile.Write(_dir, 51234, "abc123token");

        AssertLockedDownToCurrentUser(path);
    }

    [Fact]
    public void Write_OnWindows_ReplacesAnInheritedDaclOnAPreExistingFile()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows ACLs — asserted on the Windows CI leg.

        // A handshake left behind by an older build was created with the plain,
        // inherited ACL. Rewriting it must not keep that DACL.
        Directory.CreateDirectory(_dir);
        var stale = Path.Combine(_dir, "sidecar.json");
        File.WriteAllText(stale, "{\"port\":1,\"token\":\"stale\"}");
        Assert.False(new FileInfo(stale).GetAccessControl().AreAccessRulesProtected);

        var path = SidecarHandshakeFile.Write(_dir, 51234, "abc123token");

        Assert.Equal(stale, path);
        AssertLockedDownToCurrentUser(path);
    }

    [Fact]
    public void Write_OnUnix_SetsOwnerReadWriteOnlyMode()
    {
        if (OperatingSystem.IsWindows()) return; // unix file modes — asserted on macOS/Linux.

        var path = SidecarHandshakeFile.Write(_dir, 51234, "abc123token");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Write_OnUnix_TightensTheModeOfAPreExistingFile()
    {
        if (OperatingSystem.IsWindows()) return; // unix file modes — asserted on macOS/Linux.

        Directory.CreateDirectory(_dir);
        var stale = Path.Combine(_dir, "sidecar.json");
        File.WriteAllText(stale, "{\"port\":1,\"token\":\"stale\"}");
        File.SetUnixFileMode(stale,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var path = SidecarHandshakeFile.Write(_dir, 51234, "abc123token");

        Assert.Equal(stale, path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private static void AssertLockedDownToCurrentUser(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var me = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(me);

        Assert.True(security.AreAccessRulesProtected, "inheritance must be disabled on the handshake file");

        var rules = security.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        Assert.NotEmpty(rules);
        foreach (FileSystemAccessRule rule in rules)
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(me, rule.IdentityReference);
            Assert.False(rule.IsInherited);
        }
    }
}

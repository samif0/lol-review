using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Revu.Sidecar;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The sidecar handshake (<c>Revu\sidecar.json</c>) carries the per-launch bearer
/// token that gates every sidecar endpoint. It must be readable by the current
/// user only from the moment it exists, and stay that way after the atomic
/// temp-file swap in <see cref="SidecarHostSession.Publish"/>.
/// </summary>
public sealed class SidecarHandshakeFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.Handshake.Tests", Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "LoLReviewData");
    private string Handshake => Path.Combine(_root, "Revu");
    private string HandshakeFile => Path.Combine(Handshake, "sidecar.json");

    [Fact]
    public void CreateOwnerOnlyWritesThePayloadUnderACurrentUserOnlyDacl()
    {
        Directory.CreateDirectory(Handshake);
        var payload = Encoding.UTF8.GetBytes("{\"port\":45678,\"token\":\"secret\"}");

        SidecarHandshakeFile.CreateOwnerOnly(HandshakeFile, payload);

        Assert.Equal(payload, File.ReadAllBytes(HandshakeFile));
        AssertLockedDownToCurrentUser(HandshakeFile);
    }

    [Fact]
    public void CreateOwnerOnlyReplacesALeftoverFileInsteadOfKeepingItsInheritedDacl()
    {
        Directory.CreateDirectory(Handshake);
        File.WriteAllText(HandshakeFile, "leftover from a crashed write");
        Assert.False(new FileInfo(HandshakeFile).GetAccessControl().AreAccessRulesProtected);

        SidecarHandshakeFile.CreateOwnerOnly(HandshakeFile, Encoding.UTF8.GetBytes("{}"));

        Assert.Equal("{}", File.ReadAllText(HandshakeFile));
        AssertLockedDownToCurrentUser(HandshakeFile);
    }

    [Fact]
    public void PublishedHandshakeIsLockedToTheCurrentUserAfterTheAtomicSwap()
    {
        using var owner = new SidecarHostSession(Data, Handshake, "locked-launch");
        owner.Publish(45678, new string('A', 64));

        AssertLockedDownToCurrentUser(HandshakeFile);
        using var handshake = JsonDocument.Parse(File.ReadAllText(HandshakeFile));
        Assert.Equal(new string('A', 64), handshake.RootElement.GetProperty("token").GetString());
        Assert.Equal("locked-launch", handshake.RootElement.GetProperty("launchId").GetString());
        Assert.Equal(new[] { HandshakeFile }, Directory.GetFiles(Handshake));
    }

    [Fact]
    public void PublishOverAStaleInheritedHandshakeLeavesOnlyALockedFile()
    {
        // A handshake left by an older build or an exited owner carries the
        // directory's inherited ACL; replacing it must not keep that DACL.
        Directory.CreateDirectory(Handshake);
        File.WriteAllText(HandshakeFile, JsonSerializer.Serialize(new
            { port = 1, processId = int.MaxValue, launchId = "exited-launch", token = "old" }));
        Assert.False(new FileInfo(HandshakeFile).GetAccessControl().AreAccessRulesProtected);

        using var replacement = new SidecarHostSession(Data, Handshake, "replacement");
        replacement.Publish(45678, new string('A', 64));

        AssertLockedDownToCurrentUser(HandshakeFile);
        using var handshake = JsonDocument.Parse(File.ReadAllText(HandshakeFile));
        Assert.Equal("replacement", handshake.RootElement.GetProperty("launchId").GetString());
    }

    private static void AssertLockedDownToCurrentUser(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var me = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(me);
        Assert.True(security.AreAccessRulesProtected, "inheritance must be disabled on the handshake file");

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var rule = Assert.Single(rules.Cast<FileSystemAccessRule>());
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(me, rule.IdentityReference);
        Assert.False(rule.IsInherited);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

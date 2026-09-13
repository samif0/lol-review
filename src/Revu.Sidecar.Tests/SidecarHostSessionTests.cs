using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Revu.Core.Data;
using Revu.Sidecar;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class SidecarHostSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.Host.Tests", Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "LoLReviewData");
    private string Handshake => Path.Combine(_root, "Revu");
    private string HandshakeFile => Path.Combine(Handshake, "sidecar.json");

    [Fact]
    public void SecondOwnerCannotReplaceFirstHandshake()
    {
        using var owner = new SidecarHostSession(Data, Handshake, "first");
        owner.Publish(45678, new string('A', 64));
        var original = File.ReadAllText(HandshakeFile);
        Assert.Throws<IOException>(() => new SidecarHostSession(Data, Handshake, "second"));
        Assert.Equal(original, File.ReadAllText(HandshakeFile));
    }

    [Fact]
    public void PublishCarriesLaunchIdentityAndDisposeAllowsNextOwner()
    {
        using (var owner = new SidecarHostSession(Data, Handshake, "launch-one"))
        {
            owner.Publish(45678, new string('A', 64));
            using var handshake = JsonDocument.Parse(File.ReadAllText(HandshakeFile));
            Assert.Equal("launch-one", handshake.RootElement.GetProperty("launchId").GetString());
            Assert.Equal(Environment.ProcessId, handshake.RootElement.GetProperty("processId").GetInt32());
            Assert.Equal(1, handshake.RootElement.GetProperty("apiVersion").GetInt32());
            Assert.Equal(Path.GetFullPath(Data), handshake.RootElement.GetProperty("dataDirectory").GetString());
        }
        Assert.False(File.Exists(HandshakeFile));
        using var next = new SidecarHostSession(Data, Handshake, "launch-two");
    }

    [Fact]
    public void LegacyListeningEndpointIsPreservedEvenWithoutLeaseOrPid()
    {
        Directory.CreateDirectory(Handshake);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var original = JsonSerializer.Serialize(new { port = ((IPEndPoint)listener.LocalEndpoint).Port, token = "legacy" });
        File.WriteAllText(HandshakeFile, original);
        Assert.Throws<IOException>(() => new SidecarHostSession(Data, Handshake));
        Assert.Equal(original, File.ReadAllText(HandshakeFile));
    }

    [Fact]
    public void StaleLegacyClosedEndpointCanBeReplacedWithoutAnOwnerPid()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var formerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Directory.CreateDirectory(Handshake);
        var original = JsonSerializer.Serialize(new { port = formerPort, token = "legacy" });
        File.WriteAllText(HandshakeFile, original);

        // Exercise the real socket probe both before storage initialization and
        // again at publication. A dead legacy host has no PID to short-circuit it.
        using (var replacement = new SidecarHostSession(Data, Handshake, "replacement"))
        {
            Assert.Equal(original, File.ReadAllText(HandshakeFile));
            replacement.Publish(45678, new string('A', 64));
            using var handshake = JsonDocument.Parse(File.ReadAllText(HandshakeFile));
            Assert.Equal("replacement", handshake.RootElement.GetProperty("launchId").GetString());
            Assert.Equal(Environment.ProcessId, handshake.RootElement.GetProperty("processId").GetInt32());
        }
        Assert.False(File.Exists(HandshakeFile));
    }

    [Fact]
    public void LivePidWithClosedPortIsPreserved()
    {
        Directory.CreateDirectory(Handshake);
        File.WriteAllText(HandshakeFile, JsonSerializer.Serialize(new { port = 45678, processId = Environment.ProcessId }));
        Assert.Throws<IOException>(() => new SidecarHostSession(Data, Handshake));
        Assert.True(File.Exists(HandshakeFile));
    }

    [Fact]
    public void MalformedHandshakeFailsBeforePublicationAndReleasesLease()
    {
        Directory.CreateDirectory(Handshake);
        File.WriteAllText(HandshakeFile, "{");
        Assert.ThrowsAny<JsonException>(() => new SidecarHostSession(Data, Handshake));
        File.Delete(HandshakeFile);
        using var next = new SidecarHostSession(Data, Handshake);
    }

    [Fact]
    public void DisposingDoesNotRemoveAReplacedHandshake()
    {
        using (var owner = new SidecarHostSession(Data, Handshake, "original"))
        {
            owner.Publish(45678, new string('A', 64));
            File.WriteAllText(HandshakeFile, "{\"launchId\":\"different\"}");
        }
        Assert.True(File.Exists(HandshakeFile));
    }

    [Theory]
    [InlineData("revu.db")]
    [InlineData("lol_review.db")]
    public void NewerCanonicalOrLegacySchemaFailsBeforeWritesAndReleasesLease(string fileName)
    {
        Directory.CreateDirectory(Data);
        var database = Path.Combine(Data, fileName);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_metadata(key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO schema_metadata VALUES('app_schema_version', $version);
                """;
            command.Parameters.AddWithValue("$version", (Schema.CurrentAppSchemaVersion + 1).ToString());
            command.ExecuteNonQuery();
        }
        var before = SHA256.HashData(File.ReadAllBytes(database));

        Assert.Throws<InvalidDataException>(() => new SidecarHostSession(Data, Handshake, "older-backend"));

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database)));
        Assert.False(File.Exists(HandshakeFile));
        using var releasedLease = DataRootLease.Acquire(Data);
    }

    [Fact]
    public void LiveLegacyHandshakeArrivingDuringStartupIsPreservedAtPublicationAndDisposal()
    {
        string original;
        using (var owner = new SidecarHostSession(Data, Handshake, "new-host"))
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Directory.CreateDirectory(Handshake);
            original = JsonSerializer.Serialize(new
                { port = ((IPEndPoint)listener.LocalEndpoint).Port, token = "late-legacy" });
            File.WriteAllText(HandshakeFile, original);

            Assert.Throws<IOException>(() => owner.Publish(45678, new string('A', 64)));
            Assert.Equal(original, File.ReadAllText(HandshakeFile));
        }
        Assert.Equal(original, File.ReadAllText(HandshakeFile));
    }

    [Fact]
    public void SameLaunchCanRepublishItsOwnHandshake()
    {
        using var owner = new SidecarHostSession(Data, Handshake, "same-launch");
        owner.Publish(45678, new string('A', 64));
        owner.Publish(45679, new string('B', 64));
        using var handshake = JsonDocument.Parse(File.ReadAllText(HandshakeFile));
        Assert.Equal(45679, handshake.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("same-launch", handshake.RootElement.GetProperty("launchId").GetString());
    }

    [Fact]
    public void ConfirmedDeadPidAllowsRestartWithAClosedFormerPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var formerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Directory.CreateDirectory(Handshake);
        File.WriteAllText(HandshakeFile, JsonSerializer.Serialize(new
            { port = formerPort, processId = int.MaxValue, launchId = "exited-launch", token = "old" }));

        using var replacement = new SidecarHostSession(Data, Handshake, "replacement");
        replacement.Publish(45678, new string('A', 64));
        using var handshake = JsonDocument.Parse(File.ReadAllText(HandshakeFile));
        Assert.Equal("replacement", handshake.RootElement.GetProperty("launchId").GetString());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

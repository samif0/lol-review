#nullable enable
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>Owns one data directory for this process, before any services can write.</summary>
public sealed class SidecarHostSession : IDisposable
{
    public const int ApiVersion = 1;
    private readonly DataRootLease _lease;
    private readonly string _handshakePath;
    public string LaunchId { get; }
    public string DataDirectory => _lease.DataDirectory;

    public SidecarHostSession(string dataDirectory, string handshakeDirectory, string? launchId = null)
    {
        Directory.CreateDirectory(dataDirectory);
        _lease = DataRootLease.Acquire(dataDirectory);
        _handshakePath = Path.Combine(handshakeDirectory, "sidecar.json");
        LaunchId = string.IsNullOrWhiteSpace(launchId) ? Guid.NewGuid().ToString("N") : launchId;
        try
        {
            if (LaunchId.Length > 128 || LaunchId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new ArgumentException("Invalid host launch identity.", nameof(launchId));
            RejectLiveHandshake(_handshakePath);
            var database = Path.Combine(DataDirectory, "revu.db");
            if (!File.Exists(database)) database = Path.Combine(DataDirectory, AppDataMigrator.LegacyDatabaseFileName);
            if (File.Exists(database)) DatabaseSchemaCompatibility.EnsureCompatible(database);
        }
        catch { _lease.Dispose(); throw; }
    }

    public object Identity => new { apiVersion = ApiVersion, launchId = LaunchId,
        processId = Environment.ProcessId, dataDirectory = DataDirectory,
        schemaVersion = Schema.CurrentAppSchemaVersion };

    public void Publish(int port, string token)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        RejectLiveHandshake(_handshakePath, LaunchId);
        Directory.CreateDirectory(Path.GetDirectoryName(_handshakePath)!);
        var temporary = _handshakePath + "." + LaunchId + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { port, token,
                apiVersion = ApiVersion, launchId = LaunchId, processId = Environment.ProcessId,
                dataDirectory = DataDirectory, schemaVersion = Schema.CurrentAppSchemaVersion }));
            File.Move(temporary, _handshakePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLiveHandshake(string path, string? ownLaunchId = null)
    {
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (ownLaunchId != null && root.TryGetProperty("launchId", out var launchId)
            && launchId.GetString() == ownLaunchId && root.TryGetProperty("processId", out var ownPid)
            && ownPid.TryGetInt32(out var currentPid) && currentPid == Environment.ProcessId) return;
        if (root.TryGetProperty("processId", out var pid) && pid.TryGetInt32(out var id))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.HasExited) throw new IOException("Another live process owns this sidecar handshake.");
            }
            catch (ArgumentException) { return; /* recorded owner is dead; do not probe a recycled port */ }
        }
        // Older hosts did not publish a PID or hold our lease. Never replace a
        // handshake while its loopback endpoint is still listening.
        if (!root.TryGetProperty("port", out var portValue) || !portValue.TryGetInt32(out var port)
            || port is < 1 or > 65535) throw new InvalidDataException("Invalid existing sidecar handshake.");
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Windows Socket.ConnectAsync can take about two seconds to surface
        // ConnectionRefused for a closed loopback port. Give that definitive
        // refusal time to arrive; cancellation still cannot prove an owner dead.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token).AsTask().GetAwaiter().GetResult();
            throw new IOException("Another live endpoint owns this sidecar handshake.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { }
        // Timeouts, permission failures, and malformed files fail closed.
    }

    public void Dispose()
    {
        // Only remove this launch's handshake, while we still own the data lease.
        try
        {
            if (File.Exists(_handshakePath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_handshakePath));
                if (document.RootElement.TryGetProperty("launchId", out var id) && id.GetString() == LaunchId)
                    File.Delete(_handshakePath);
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        finally { _lease.Dispose(); }
    }
}

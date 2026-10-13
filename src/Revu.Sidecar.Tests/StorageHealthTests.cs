using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// A failed startup schema pass shows up in /api/health (storage "degraded") instead of
/// the database looking current. The top-level status stays "ready": the desktop host
/// gates launch on it and the degraded-start policy keeps the app usable.
/// </summary>
public sealed class StorageHealthTests
{
    [Fact]
    public async Task Health_AfterAFailedSchemaUpgrade_ReportsDegradedStorage()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var storage = new StorageStartupHealth();
        storage.MarkSchemaUpgradeFailed();

        var (statusCode, body) = SidecarEndpoints.Health(scope.ConnectionFactory, storage, NullLogger.Instance);

        var json = JsonSerializer.SerializeToElement(body);
        Assert.Equal(200, statusCode);
        Assert.Equal("ready", json.GetProperty("status").GetString());
        Assert.Equal("degraded", json.GetProperty("storage").GetString());
        Assert.Equal("schema_upgrade_failed", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Health_AfterACleanStart_ReportsStorageOk()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var (statusCode, body) = SidecarEndpoints.Health(scope.ConnectionFactory, new StorageStartupHealth(), NullLogger.Instance);

        var json = JsonSerializer.SerializeToElement(body);
        Assert.Equal(200, statusCode);
        Assert.Equal("ok", json.GetProperty("storage").GetString());
    }
}

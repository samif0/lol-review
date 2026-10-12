using Microsoft.Extensions.Logging;
using Revu.Core.Data;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class SidecarFileLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.FileLog.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultDirectoryFollowsTheDataRootOverride()
    {
        Assert.Equal(Path.Combine(AppDataPaths.SidecarHandshakeDirectory, "logs"), SidecarFileLoggerProvider.DefaultDirectory);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(SidecarFileLoggerProvider.DefaultDirectory),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WritesInformationAndAboveWithCategoryAndException()
    {
        using var provider = new SidecarFileLoggerProvider(_root);
        var logger = provider.CreateLogger("Revu.Sidecar.Startup");
        logger.LogDebug("hidden detail");
        logger.LogInformation("Sidecar listening on {Port}", 4242);
        logger.LogError(new IOException("disk full"), "Failed to persist live game end for {GameId}", 7);

        var text = File.ReadAllText(provider.CurrentPath);
        Assert.DoesNotContain("hidden detail", text);
        Assert.Contains("INFO  Revu.Sidecar.Startup: Sidecar listening on 4242", text);
        Assert.Contains("ERROR Revu.Sidecar.Startup: Failed to persist live game end for 7", text);
        Assert.Contains("System.IO.IOException: disk full", text);
    }

    [Fact]
    public void FrameworkChatterIsFilteredButItsWarningsAreKept()
    {
        using var provider = new SidecarFileLoggerProvider(_root);
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(provider);
            builder.AddFilter<SidecarFileLoggerProvider>("Microsoft", LogLevel.Warning);
        });
        factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation("Request starting GET /api/vod?gameId=1");
        factory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel").LogWarning("Connection reset");
        factory.CreateLogger("Revu.Sidecar.Program").LogInformation("Bookmark added");

        var text = File.ReadAllText(provider.CurrentPath);
        Assert.DoesNotContain("Request starting", text);
        Assert.Contains("Connection reset", text);
        Assert.Contains("Bookmark added", text);
    }

    [Fact]
    public void EmailsAndCredentialsAreMaskedBeforeWriting()
    {
        using var provider = new SidecarFileLoggerProvider(_root);
        provider.CreateLogger("Auth").LogWarning(
            "Login for player.one+lol@example.co.uk failed; header Bearer {Token}; url /x?token=abc123&code=999111; key RGAPI-1234-abcd",
            new string('A', 64));

        var text = File.ReadAllText(provider.CurrentPath);
        Assert.DoesNotContain("player.one", text);
        Assert.DoesNotContain(new string('A', 64), text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("999111", text);
        Assert.DoesNotContain("RGAPI-1234", text);
        Assert.Contains("[email]", text);
        Assert.Contains("Bearer [redacted]", text);
        Assert.Contains("token=[redacted]", text);
    }

    [Fact]
    public void RollsOverAtTheSizeCapAndKeepsOnlyTheConfiguredGenerations()
    {
        using (var provider = new SidecarFileLoggerProvider(_root, maxBytes: 4096, maxFiles: 3))
        {
            var logger = provider.CreateLogger("Roll");
            for (var i = 0; i < 400; i++) logger.LogInformation("line {Index} {Padding}", i, new string('x', 40));
        }

        var files = Directory.GetFiles(_root).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "sidecar.1.log", "sidecar.2.log", "sidecar.log" }, files);
        foreach (var file in Directory.GetFiles(_root)) Assert.True(new FileInfo(file).Length <= 4096, file);
        Assert.Contains("line 399 ", File.ReadAllText(Path.Combine(_root, "sidecar.log")));
        Assert.DoesNotContain("line 0 ", string.Concat(Directory.GetFiles(_root).Select(File.ReadAllText)));
    }

    [Fact]
    public void ReadersCanOpenTheLiveFileAndAnUnwritableLocationNeverThrows()
    {
        using (var provider = new SidecarFileLoggerProvider(_root))
        {
            provider.CreateLogger("Share").LogInformation("first");
            using (var reader = new FileStream(provider.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                Assert.True(reader.Length > 0);
            provider.CreateLogger("Share").LogInformation("second");
            Assert.Contains("second", File.ReadAllText(provider.CurrentPath));
        }

        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "a file where the log folder should be");
        using var broken = new SidecarFileLoggerProvider(blocked);
        broken.CreateLogger("Broken").LogError("still running");
        broken.WriteUnhandled(new InvalidOperationException("boom"));
    }

    [Fact]
    public void UnhandledExceptionsAreRecordedAndDisposalStopsWriting()
    {
        var provider = new SidecarFileLoggerProvider(_root);
        provider.WriteUnhandled(new InvalidOperationException("startup exploded"));
        provider.Dispose();
        provider.CreateLogger("Late").LogError("after dispose");
        provider.Dispose();

        var text = File.ReadAllText(provider.CurrentPath);
        Assert.Contains("CRIT  Unhandled: Unhandled exception; the sidecar is terminating", text);
        Assert.Contains("startup exploded", text);
        Assert.DoesNotContain("after dispose", text);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }
}

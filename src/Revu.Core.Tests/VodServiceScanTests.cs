using System.Runtime.InteropServices;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

/// <summary>
/// VOD folder scan boundaries: the walk must stay inside the chosen folder and
/// must terminate even when the tree contains directory links back into itself.
/// </summary>
public sealed class VodServiceScanTests : IDisposable
{
    private readonly string _root;

    public VodServiceScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task FindRecordingsAsync_DoesNotFollowDirectoryLinkOutsideScanRoot()
    {
        var ascent = Path.Combine(_root, "Ascent");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(ascent);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(ascent, "real.mp4"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(outside, "foreign.mp4"), [1, 2, 3, 4]);

        if (!TryCreateDirectoryLink(Path.Combine(ascent, "link"), outside))
            return;

        using var scope = new TestDatabaseScope();
        var service = BuildService(scope, ascent);

        var recordings = await service.FindRecordingsAsync();

        var names = recordings.Select(r => r.Name).ToList();
        Assert.Contains("real.mp4", names);
        Assert.DoesNotContain("foreign.mp4", names);
        Assert.All(recordings, r => Assert.StartsWith(ascent, r.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindRecordingsAsync_SelfReferencingLink_TerminatesAndReturnsEachFileOnce()
    {
        var ascent = Path.Combine(_root, "Ascent");
        Directory.CreateDirectory(ascent);
        File.WriteAllBytes(Path.Combine(ascent, "real.mp4"), [1, 2, 3, 4]);

        if (!TryCreateDirectoryLink(Path.Combine(ascent, "loop"), ascent))
            return;

        using var scope = new TestDatabaseScope();
        var service = BuildService(scope, ascent);

        var scan = service.FindRecordingsAsync();
        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, scan), "scan did not complete within 10s");
        var recordings = await scan;
        Assert.Single(recordings);
        Assert.Equal("real.mp4", recordings[0].Name);
    }

    private static VodService BuildService(TestDatabaseScope scope, string ascentFolder) =>
        new(
            scope.Games,
            scope.Vod,
            new TestConfigService(new AppConfig { AscentFolder = ascentFolder }),
            NullLogger<VodService>.Instance);

    /// <summary>
    /// Creates a directory symbolic link. On Windows, creating symlinks needs
    /// Developer Mode or SeCreateSymbolicLinkPrivilege, which CI runners may
    /// lack; in that case the test is skipped (returns true only on success).
    /// On macOS/Linux the link is always created and a failure propagates.
    /// </summary>
    private static bool TryCreateDirectoryLink(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

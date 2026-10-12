using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The shared path policy: only fully qualified local paths are trusted, and folder
/// overlap covers equal, containing and contained folders.
/// </summary>
public sealed class LocalPathPolicyTests
{
    [WindowsFact]
    public void LocalAbsolutePaths_AreTrusted_AndNormalized()
    {
        Assert.True(LocalPathPolicy.TryGetLocalFullPath(@"C:\Videos\..\Videos\game.mp4", out var full));
        Assert.Equal(@"C:\Videos\game.mp4", full);
        Assert.True(LocalPathPolicy.IsLocal("D:/Ascent/game.mkv"));
    }

    [WindowsTheory]
    [InlineData(@"\\attacker\share\x.mp4")]
    [InlineData(@"//attacker/share/x.mp4")]
    [InlineData(@"\\?\UNC\attacker\share\x.mp4")]
    [InlineData(@"\\?\C:\Videos\x.mp4")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"C:\Videos\x.mp4:hidden")]
    [InlineData(@"Videos\x.mp4")]
    [InlineData(@"C:x.mp4")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NetworkDeviceRelativeAndStreamPaths_AreRejected(string? path)
    {
        Assert.False(LocalPathPolicy.TryGetLocalFullPath(path, out var full));
        Assert.Equal("", full);
    }

    [WindowsTheory]
    [InlineData(@"C:\Videos", @"C:\Videos", true)]
    [InlineData(@"C:\Videos\", @"c:\videos", true)]
    [InlineData(@"C:\Videos", @"C:\Videos\Clips", true)]
    [InlineData(@"C:\Videos\Clips", @"C:\Videos", true)]
    [InlineData(@"C:\", @"C:\Videos", true)]
    [InlineData(@"C:\Videos", @"C:\VideosClips", false)]
    [InlineData(@"C:\Videos", @"D:\Videos", false)]
    [InlineData(@"C:\Videos", "", false)]
    public void FoldersOverlap_CoversSameContainingAndContained(string a, string b, bool expected)
    {
        Assert.Equal(expected, LocalPathPolicy.FoldersOverlap(a, b));
        Assert.Equal(expected, LocalPathPolicy.FoldersOverlap(b, a));
    }
}

/// <summary>A [Theory] that runs only on Windows (drive-letter and UNC path semantics).</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Pins Windows path semantics; runs on the windows-latest CI job.";
        }
    }
}

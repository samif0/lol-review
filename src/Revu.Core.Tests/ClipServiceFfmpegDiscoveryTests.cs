using System.Text.RegularExpressions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Contract for the well-known ffmpeg locations probed after the bundled copy and
/// PATH. Every candidate must live under a directory the current user or an
/// administrator controls (the user's own profile, Program Files); nothing directly
/// under a drive root, where any local account can create folders by default.
/// </summary>
public sealed class ClipServiceFfmpegDiscoveryTests
{
    private const string LocalAppData = @"C:\Users\alice\AppData\Local";
    private const string ProgramFiles = @"C:\Program Files";
    private const string UserProfile = @"C:\Users\alice";

    private static readonly Regex DriveRootChild = new(@"^[A-Za-z]:\\[^\\]+\\", RegexOptions.Compiled);

    [Fact]
    public void NoCandidateIsDirectlyUnderADriveRoot()
    {
        var candidates = ClipService.FfmpegCandidatePaths(LocalAppData, ProgramFiles, UserProfile, wingetHit: null);

        var offenders = candidates
            .Where(p => DriveRootChild.IsMatch(p) && !p.StartsWith(LocalAppData, StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith(ProgramFiles, StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith(UserProfile, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Drive-root candidates are writable by any local user: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RemainingCandidatesKeepTheirOrder()
    {
        const string winget = @"C:\Users\alice\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_x\ffmpeg-7\bin\ffmpeg.exe";

        var candidates = ClipService.FfmpegCandidatePaths(LocalAppData, ProgramFiles, UserProfile, winget);

        Assert.Equal(
            [
                Path.Combine(LocalAppData, "Revu", "ffmpeg.exe"),
                Path.Combine(ProgramFiles, "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(UserProfile, "scoop", "shims", "ffmpeg.exe"),
                winget,
            ],
            candidates);
    }

    [Fact]
    public void NullWingetHitIsOmitted()
    {
        var candidates = ClipService.FfmpegCandidatePaths(LocalAppData, ProgramFiles, UserProfile, wingetHit: null);

        Assert.DoesNotContain(null, candidates);
        Assert.Equal(3, candidates.Count);
    }
}

using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Contract for the well-known ffmpeg locations probed after the bundled copy and
/// PATH. Every candidate must live under a directory the current user or an
/// administrator controls (the user's own profile, Program Files); nothing directly
/// under a drive root, where any local account can create folders by default.
/// </summary>
public sealed class FfmpegDiscoveryTests
{
    private const string LocalAppData = @"C:\Users\alice\AppData\Local";
    private const string ProgramFiles = @"C:\Program Files";
    private const string UserProfile = @"C:\Users\alice";

    [Fact]
    public void EveryCandidateLivesUnderAUserOrAdminOwnedRoot()
    {
        var candidates = FfmpegRunner.FfmpegCandidatePaths(LocalAppData, ProgramFiles, UserProfile, wingetHit: null);

        var offenders = candidates
            .Where(p => !new[] { LocalAppData, ProgramFiles, UserProfile }
                .Any(root => p.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Drive-root candidates are writable by any local user: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RemainingCandidatesKeepTheirOrder()
    {
        const string winget = @"C:\Users\alice\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_x\ffmpeg-7\bin\ffmpeg.exe";

        var candidates = FfmpegRunner.FfmpegCandidatePaths(LocalAppData, ProgramFiles, UserProfile, winget);

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
        var candidates = FfmpegRunner.FfmpegCandidatePaths(LocalAppData, ProgramFiles, UserProfile, wingetHit: null);

        Assert.DoesNotContain(null, candidates);
        Assert.Equal(3, candidates.Count);
    }

    [Fact]
    public void PathLookupSkipsRelativeEntriesThatResolveAgainstTheCurrentDirectory()
    {
        var name = "ffmpeg-path-" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Environment.CurrentDirectory, name);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), []);

            Assert.Null(FfmpegRunner.FindInPath("ffmpeg", name));
            Assert.Null(FfmpegRunner.FindInPath("ffmpeg", @".\" + name));
            Assert.Equal(Path.Combine(dir, "ffmpeg.exe"), FfmpegRunner.FindInPath("ffmpeg", name + Path.PathSeparator + dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

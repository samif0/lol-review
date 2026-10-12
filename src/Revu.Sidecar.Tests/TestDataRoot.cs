using System.Runtime.CompilerServices;
using Revu.Core.Data;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Points every process-global data path (AppDataPaths: config.json, revu.db,
/// clips, backups, the sidecar handshake) at a throw-away folder BEFORE any
/// test can touch it, so no sidecar test can reach the developer's live
/// %LOCALAPPDATA%\LoLReviewData. Mirrors Revu.Core.Tests.TestDataRoot.
/// </summary>
internal static class TestDataRoot
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "data-root-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Isolate()
    {
        // A pre-set REVU_DATA_ROOT survives only when it is itself a temp folder; a
        // real data root exported in the shell stops the run before any test writes.
        var preset = Environment.GetEnvironmentVariable("REVU_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(preset))
        {
            if (IsUnderTemp(preset)) return;
            throw new InvalidOperationException(
                $"REVU_DATA_ROOT is set to '{preset}', outside the temp folder. Unset it before running tests.");
        }
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("REVU_DATA_ROOT", Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        };
    }

    internal static bool IsUnderTemp(string path)
    {
        try
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public sealed class TestDataRootTests
{
    [Fact]
    public void EveryProcessGlobalPath_LivesUnderATempRoot_NeverTheLiveAppData()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        foreach (var path in new[] { AppDataPaths.UserDataRoot, AppDataPaths.ConfigPath, AppDataPaths.DatabasePath, AppDataPaths.ClipsDirectory, AppDataPaths.BackupsDirectory, AppDataPaths.SidecarHandshakeDirectory })
        {
            Assert.StartsWith(temp, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void OnlyAFolderInsideTemp_CountsAsAnIsolatedRoot()
    {
        var temp = Path.GetTempPath();
        Assert.True(TestDataRoot.IsUnderTemp(Path.Combine(temp, "scratch", "root")));
        Assert.False(TestDataRoot.IsUnderTemp(temp));
        Assert.False(TestDataRoot.IsUnderTemp(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoLReviewData")));
        Assert.False(TestDataRoot.IsUnderTemp(temp.TrimEnd(Path.DirectorySeparatorChar) + "-sibling"));
    }
}

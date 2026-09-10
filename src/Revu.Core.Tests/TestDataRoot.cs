using System.Runtime.CompilerServices;
using Revu.Core.Data;

namespace Revu.Core.Tests;

/// <summary>
/// Points every process-global data path (AppDataPaths: config.json, revu.db,
/// clips, backups, the sidecar handshake) at a throw-away folder BEFORE any
/// test can touch it. Without this, code that reads AppDataPaths directly while
/// a test injects a temp database (BackupService.ResetAllDataAsync did exactly
/// that) reaches the developer's live %LOCALAPPDATA%\LoLReviewData — which is
/// how the real config.json got deleted on every local test run.
/// </summary>
internal static class TestDataRoot
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "data-root-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Isolate()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REVU_DATA_ROOT"))) return;
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("REVU_DATA_ROOT", Root);
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
}

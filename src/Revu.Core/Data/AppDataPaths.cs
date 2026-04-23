#nullable enable

namespace Revu.Core.Data;

/// <summary>
/// Centralized paths for install-owned files and user-owned data.
/// User data must not live under the Velopack install root because reinstall or
/// uninstall may recreate that directory tree.
/// </summary>
public static class AppDataPaths
{
    private static readonly string LocalAppDataRoot = GetLocalAppDataRoot();

    /// <summary>
    /// Velopack install root. This tree is installer-owned. Named "LoLReview"
    /// because that matches the Velopack <c>packId</c> — changing that would
    /// break auto-update for existing installs, so this name is permanent.
    /// </summary>
    public static string InstallRoot => Path.Combine(LocalAppDataRoot, "LoLReview");

    /// <summary>User-owned data root. This tree must survive reinstall/update.</summary>
    public static string UserDataRoot => Path.Combine(LocalAppDataRoot, "RevuData");

    public static string DatabasePath => Path.Combine(UserDataRoot, "revu.db");

    public static string ConfigPath => Path.Combine(UserDataRoot, "config.json");

    public static string ClipsDirectory => Path.Combine(UserDataRoot, "clips");

    public static string BackupsDirectory => Path.Combine(UserDataRoot, "backups");

    public static IEnumerable<string> EnumerateLegacyDatabasePaths()
    {
        yield return Path.Combine(InstallRoot, "data", "lol_review.db");
        yield return Path.Combine(InstallRoot, "lol_review.db");
    }

    public static IEnumerable<string> EnumerateLegacyConfigPaths()
    {
        yield return Path.Combine(InstallRoot, "data", "config.json");
        yield return Path.Combine(InstallRoot, "config.json");
    }

    public static IEnumerable<string> EnumerateLegacyBackupDirectories()
    {
        yield return Path.Combine(InstallRoot, "data", "backups");
    }

    private static string GetLocalAppDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            return localAppData;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "Local");
    }
}

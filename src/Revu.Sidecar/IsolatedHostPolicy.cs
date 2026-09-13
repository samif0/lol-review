using System.Text.Json;
using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>Keep scratch host experiments from acting on original media or accounts.</summary>
public static class IsolatedHostPolicy
{
    public static void ValidateRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("Scratch root must be absolute.");
        var full = DataRootLease.Canonicalize(root);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Within(full, local)) throw new InvalidOperationException("Isolated host testing requires storage outside production LocalAppData.");
    }

    public static void ValidateConfiguration(string directory)
    {
        // A snapshot preserves its config exactly; it is not automatically safe
        // to execute. Require an independently prepared scratch configuration.
        directory = DataRootLease.Canonicalize(directory);
        // Default outputs and SQLite/config side files can otherwise redirect
        // writes even when the root itself is an ordinary directory.
        foreach (var name in new[] { "backups", "clips", "revu.db", "revu.db-wal", "revu.db-shm",
            "lol_review.db", "lol_review.db-wal", "lol_review.db-shm",
            "protected_secrets.bin", "protected_secrets.bin.tmp", "config.json.tmp" })
            RejectAlias(Path.Combine(directory, name));

        var scratchRoot = Path.GetDirectoryName(directory)!;
        var legacyDirectories = new[] { Path.Combine(scratchRoot, "LoLReview"), Path.Combine(scratchRoot, "LoLReview", "data") };
        foreach (var legacy in legacyDirectories)
        {
            RejectAlias(legacy);
            RejectAlias(Path.Combine(legacy, "lol_review.db"));
        }
        foreach (var configDirectory in new[] { directory }.Concat(legacyDirectories))
        foreach (var filename in new[] { "config.json", "config.json.bak", "config.ini" })
        {
            var file = Path.Combine(configDirectory, filename);
            RejectAlias(file);
            if (!File.Exists(file)) continue;
            if (filename == "config.ini") throw new InvalidOperationException("Prepare a scratch JSON configuration before host testing.");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Scratch configuration must be a JSON object.");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = property.Name.Replace("_", "").ToLowerInvariant();
                if (key is not ("clipsfolder" or "backupfolder")) continue;
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!Path.IsPathFullyQualified(value) || !Within(Path.GetFullPath(value), directory))
                    throw new InvalidOperationException("Scratch config output folders must be inside its LoLReviewData directory.");
                RejectAlias(value);
            }
        }
    }

    private static void RejectAlias(string path)
    {
        DataRootLease.Canonicalize(path);
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Scratch host files and outputs cannot be directory or file aliases.");
    }

    private static bool Within(string path, string root) =>
        path.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static bool Allows(string method, string route)
    {
        // Database-only editing is available. Disable operations that may write
        // referenced media, change output paths, contact accounts, control the
        // League client, restore files, or invoke the installed updater.
        route = route.TrimEnd('/');
        // ASP.NET route matching is case-insensitive. Match that behavior here.
        if (route.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)
            || route.StartsWith("/api/update", StringComparison.OrdinalIgnoreCase)
            // The pregame static-data client's cache currently ignores REVU_DATA_ROOT.
            || route.Equals("/api/pregame", StringComparison.OrdinalIgnoreCase)) return false;
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return ReadRoutes.Contains(route);
        return method.Equals("POST", StringComparison.OrdinalIgnoreCase) && DatabaseWriteRoutes.Contains(route);
    }

    // Exact reviewed routes prevent a later endpoint with file/network effects
    // from silently inheriting permission merely by sharing a prefix.
    private static readonly HashSet<string> DatabaseWriteRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/host/shutdown",
        "/api/objective/create", "/api/objective/update", "/api/objective/delete", "/api/objective/priority", "/api/objective/complete",
        "/api/review/save", "/api/review/skip", "/api/review/delete", "/api/review/draft/save",
        "/api/bookmark/add", "/api/bookmark/note", "/api/bookmark/delete", "/api/bookmark/objective", "/api/bookmark/tag", "/api/bookmark/quality",
        "/api/evidence/polarity", "/api/evidence/objective", "/api/evidence/prompt", "/api/evidence/status",
        "/api/event/correct", "/api/correction/revert", "/api/death/classify", "/api/death/clear",
        "/api/prompt/answer/save", "/api/focus-adherence"
    };

    private static readonly HashSet<string> ReadRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health", "/api/host", "/api/dashboard", "/api/events", "/api/games", "/api/objectives",
        "/api/objective/games", "/api/objective/notes", "/api/objective", "/api/vod", "/api/derived", "/api/review",
        "/api/rules", "/api/tiltcheck", "/api/matchups", "/api/matchups/export", "/api/patterns", "/api/config",
        "/api/settings/status", "/api/settings/export", "/api/review/export", "/api/stint", "/api/corrections",
        "/api/corrections/export", "/api/objectives/active", "/api/diagnostics/practice/status"
    };
}

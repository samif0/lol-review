#nullable enable

using System.Collections.Concurrent;

namespace Revu.App.Helpers;

internal static class FileProbeCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool Exists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(path, out var cached) && now - cached.CheckedAt <= DefaultTtl)
        {
            return cached.Exists;
        }

        var exists = File.Exists(path);
        Cache[path] = new Entry(exists, now);
        return exists;
    }

    public static void Invalidate(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Cache.TryRemove(path, out _);
        }
    }

    private sealed record Entry(bool Exists, DateTimeOffset CheckedAt);
}

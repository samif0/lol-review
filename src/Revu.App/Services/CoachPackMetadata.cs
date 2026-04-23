#nullable enable

using System.Text.Json;

namespace Revu.App.Services;

/// <summary>
/// Helpers for reading the manifest.json / computing disk size of an
/// installed coach pack. Shared between core + ML installers so both
/// report version/size the same way.
/// </summary>
internal static class CoachPackMetadata
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static string? ReadVersion(string packRoot)
    {
        try
        {
            var path = Path.Combine(packRoot, "manifest.json");
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static long ComputeSizeBytes(string packRoot)
    {
        try
        {
            if (!Directory.Exists(packRoot)) return 0;
            long total = 0;
            // EnumerateFiles avoids loading the full list into memory
            // before summing. Even at 230 MB / ~15k files, this takes
            // under 100ms on a warm cache.
            foreach (var file in Directory.EnumerateFiles(packRoot, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}

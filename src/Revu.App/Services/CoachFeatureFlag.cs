#nullable enable

using System.Text.Json;
using Revu.Core.Data;

namespace Revu.App.Services;

/// <summary>
/// Gate for the AI Coach surface (alpha).
///
/// Sources, highest priority first:
///   1. Environment variable LOLREVIEW_ENABLE_COACH=1 (dev override)
///   2. Settings toggle persisted in config.json under "CoachEnabled"
///   3. Default: false (hidden)
///
/// Used by Shell sidebar, Review page, Objectives page to conditionally
/// show coach entry points. Check is cheap (reads env + one JSON file
/// once per call, cached after first read) so can be called in binding
/// paths.
/// </summary>
public static class CoachFeatureFlag
{
    private const string EnvVarName = "LOLREVIEW_ENABLE_COACH";
    private const string ConfigKey = "CoachEnabled";

    private static bool? _cached;
    private static readonly object _gate = new();

    /// <summary>True if the coach UI is allowed to render.</summary>
    public static bool IsEnabled()
    {
        if (_cached is bool cached) return cached;
        lock (_gate)
        {
            _cached ??= ComputeEnabled();
            return _cached.Value;
        }
    }

    /// <summary>Flip the setting (and refresh cached value). Persists to config.json.</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            var path = Path.Combine(AppDataPaths.UserDataRoot, "config.json");
            Dictionary<string, object?> config;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                    ?? new Dictionary<string, object?>();
            }
            else
            {
                config = new Dictionary<string, object?>();
                Directory.CreateDirectory(AppDataPaths.UserDataRoot);
            }

            config[ConfigKey] = enabled;
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort persist; env var still works.
        }

        lock (_gate) { _cached = enabled; }
    }

    /// <summary>Reset cache so the next IsEnabled() re-reads env + config.</summary>
    public static void Invalidate()
    {
        lock (_gate) { _cached = null; }
    }

    private static bool ComputeEnabled()
    {
        // 1. Env var
        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (string.Equals(env, "1", StringComparison.Ordinal) ||
            string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. config.json
        try
        {
            var path = Path.Combine(AppDataPaths.UserDataRoot, "config.json");
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(ConfigKey, out var prop))
            {
                return prop.ValueKind == JsonValueKind.True;
            }
        }
        catch
        {
            // Malformed config or IO error — leave disabled.
        }

        return false;
    }
}

#nullable enable

using System.Diagnostics.CodeAnalysis;

namespace LoLReview.Core.Services;

internal static class CoachPythonRuntime
{
    private static readonly string[] PreferredPythonRelativePaths =
    {
        Path.Combine(".venv-coach-qwen", "Scripts", "python.exe"),
        Path.Combine(".venv-coach-qwen", "bin", "python.exe"),
        Path.Combine(".venv-coach-qwen", "bin", "python"),
        Path.Combine(".venv", "Scripts", "python.exe"),
        Path.Combine(".venv", "bin", "python.exe"),
        Path.Combine(".venv", "bin", "python"),
    };

    public static string ResolvePythonExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("LOLREVIEW_COACH_PYTHON");
        if (TryResolveExistingFile(overridePath, out var configuredPython))
        {
            return configuredPython;
        }

        foreach (var root in EnumerateCoachRepoRoots())
        {
            foreach (var relativePath in PreferredPythonRelativePaths)
            {
                var candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "python";
    }

    public static string ResolveCoachLabScriptPath(string fileName)
    {
        foreach (var root in EnumerateCoachRepoRoots())
        {
            var candidate = Path.Combine(root, "experiments", "coach_lab", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find coach lab script: {fileName}");
    }

    private static IEnumerable<string> EnumerateCoachRepoRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = AppContext.BaseDirectory;

        for (var i = 0; i < 10 && !string.IsNullOrWhiteSpace(current); i++)
        {
            if (seen.Add(current))
            {
                yield return current;
            }

            if (Directory.Exists(Path.Combine(current, "experiments", "coach_lab")))
            {
                yield break;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }
    }

    private static bool TryResolveExistingFile(
        string? path,
        [NotNullWhen(true)] out string? resolvedPath)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            resolvedPath = Path.GetFullPath(path);
            return true;
        }

        resolvedPath = null;
        return false;
    }
}

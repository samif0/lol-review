#nullable enable

namespace Revu.Core.Services;

internal static class CoreDiagnostics
{
    private static readonly Lazy<bool> VerboseFileLoggingEnabled = new(() =>
        string.Equals(
            Environment.GetEnvironmentVariable("LOLREVIEW_DIAG_LOGS"),
            "1",
            StringComparison.Ordinal));

    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Revu");

    private static string StartupLogPath => Path.Combine(LogDirectory, "startup.log");

    public static void WriteVerbose(string message)
    {
        if (!VerboseFileLoggingEnabled.Value)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        File.AppendAllText(
            StartupLogPath,
            $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
    }
}

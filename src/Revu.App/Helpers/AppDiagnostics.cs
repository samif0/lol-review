using System.Diagnostics;

namespace Revu.App.Helpers;

internal static class AppDiagnostics
{
    private static readonly Lazy<bool> VerboseFileLoggingEnabled = new(() =>
        Debugger.IsAttached ||
        string.Equals(
            Environment.GetEnvironmentVariable("LOLREVIEW_DIAG_LOGS"),
            "1",
            StringComparison.Ordinal));

    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Revu");

    private static string CrashLogPath => Path.Combine(LogDirectory, "crash.log");

    public static void WriteVerbose(string fileName, string message)
    {
        // Coach-related diagnostics always append to coach-host.log so we
        // never lose the credential injection / sidecar attach trail. Other
        // verbose files still honor the legacy debugger/env-var gate.
        var alwaysLog = fileName.Contains("coach", StringComparison.OrdinalIgnoreCase)
            || message.Contains("CoachSidecarService", StringComparison.OrdinalIgnoreCase);

        if (!alwaysLog && !VerboseFileLoggingEnabled.Value)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        var target = alwaysLog ? "coach-host.log" : fileName;
        File.AppendAllText(
            Path.Combine(LogDirectory, target),
            $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
    }

    public static void WriteCrash(Exception exception)
    {
        Directory.CreateDirectory(LogDirectory);
        File.AppendAllText(
            CrashLogPath,
            $"[{DateTime.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}");
    }

    public static void WriteCrash(string message)
    {
        Directory.CreateDirectory(LogDirectory);
        File.AppendAllText(
            CrashLogPath,
            $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
    }
}

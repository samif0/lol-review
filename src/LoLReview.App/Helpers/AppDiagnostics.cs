using System.Diagnostics;

namespace LoLReview.App.Helpers;

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
        "LoLReview");

    private static string CrashLogPath => Path.Combine(LogDirectory, "crash.log");

    public static void WriteVerbose(string fileName, string message)
    {
        if (!VerboseFileLoggingEnabled.Value)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        File.AppendAllText(
            Path.Combine(LogDirectory, fileName),
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

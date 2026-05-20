#nullable enable

using System.Diagnostics;

namespace Revu.App.Helpers;

internal static class PerformanceTrace
{
    public static IDisposable Time(string name, string? detail = null) => new Scope(name, detail);

    public static void Mark(string name, string? detail = null)
    {
        AppDiagnostics.WriteVerbose("performance.log", Format(name, 0, detail));
    }

    private static string Format(string name, long elapsedMilliseconds, string? detail)
    {
        return string.IsNullOrWhiteSpace(detail)
            ? $"{name} {elapsedMilliseconds}ms"
            : $"{name} {elapsedMilliseconds}ms {detail}";
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _name;
        private readonly string? _detail;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public Scope(string name, string? detail)
        {
            _name = name;
            _detail = detail;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            AppDiagnostics.WriteVerbose("performance.log", Format(_name, _stopwatch.ElapsedMilliseconds, _detail));
        }
    }
}

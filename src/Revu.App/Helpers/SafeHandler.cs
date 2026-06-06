#nullable enable

using System;
using System.Threading.Tasks;

namespace Revu.App.Helpers;

/// <summary>
/// Wraps fire-and-forget async handlers (messenger callbacks, <c>async void</c> UI
/// event handlers) so an exception after the first <c>await</c> is logged instead of
/// escaping to the process-level unhandled-exception handler and crashing the app.
///
/// WinUI 3 does NOT swallow exceptions thrown in <c>async void</c> handlers — an
/// unhandled DB-locked / null-DataContext throw terminates the whole process. Route
/// every such handler through <see cref="Run"/> to make that failure mode survivable.
/// </summary>
public static class SafeHandler
{
    /// <summary>
    /// Runs the handler and logs (via <see cref="AppDiagnostics.WriteCrash(Exception)"/>)
    /// any exception instead of letting it crash the app. Cancellations are ignored.
    /// </summary>
    public static async void Run(Func<Task> handler, string context)
    {
        try
        {
            await handler().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected on navigation/teardown — not a crash.
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteCrash($"[SafeHandler] {context} failed: {ex}");
        }
    }
}

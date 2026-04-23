using System.Runtime.InteropServices;
using Revu.App.Helpers;
using Revu.Core.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using WinRT;

namespace Revu.App;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    static void Main(string[] args)
    {
        // Velopack MUST run first — handles install/update/uninstall hooks and exits early.
        //
        // Shortcut cleanup: Velopack v0.0.1298 creates TWO shortcuts by default —
        // one named after packId (Revu.lnk) and one named after mainExe
        // (Revu.App.lnk). Remove the exe-named redundant one on install/update
        // so the user only sees a single shortcut. The packId shortcut stays.
        VelopackApp.Build()
            .OnAfterInstallFastCallback((v) => RemoveRedundantExeShortcut())
            .OnAfterUpdateFastCallback((v) => RemoveRedundantExeShortcut())
            .Run();

        try
        {
            AppDiagnostics.WriteVerbose("startup.log", "Program.Main starting");

            // DB filename migration: rename lol_review.db → revu.db inside
            // the user data folder, before DI / SqliteConnectionFactory opens
            // any connection (once open, the file cannot be atomically moved).
            try
            {
                var result = AppDataMigrator.RunIfNeeded(AppDataPaths.UserDataRoot, NullLogger.Instance);
                AppDiagnostics.WriteVerbose("startup.log", $"AppData migration result: {result}");
            }
            catch (Exception migEx)
            {
                // Best-effort. If migration fails the app will fall back to
                // reading the legacy lol_review.db via the fallback logic in
                // SqliteConnectionFactory; no user data is destroyed.
                AppDiagnostics.WriteVerbose("startup.log", $"AppData migration threw: {migEx.Message}");
            }

            XamlCheckProcessRequirements();
            AppDiagnostics.WriteVerbose("startup.log", "XamlCheckProcessRequirements passed");

            ComWrappersSupport.InitializeComWrappers();
            AppDiagnostics.WriteVerbose("startup.log", "ComWrappers initialized");

            Application.Start(p =>
            {
                AppDiagnostics.WriteVerbose("startup.log", "Application.Start callback");
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
                AppDiagnostics.WriteVerbose("startup.log", "App created");
            });

            AppDiagnostics.WriteVerbose("startup.log", "Application.Start returned (app closed)");
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteCrash(ex);
        }
    }

    /// <summary>
    /// Deletes shortcuts left by prior versions so upgraders don't end up with
    /// duplicates next to the new Revu.lnk that Velopack creates from packTitle.
    /// Best-effort; silent on failure.
    /// </summary>
    private static void RemoveRedundantExeShortcut()
    {
        try
        {
            var s = new Velopack.Windows.Shortcuts();
            var loc = Velopack.Windows.ShortcutLocation.Desktop
                    | Velopack.Windows.ShortcutLocation.StartMenu;
            s.DeleteShortcuts("LoLReview.App.exe", loc);
            s.DeleteShortcuts("LoLReview.exe", loc);
        }
        catch
        {
            // Best-effort; nothing to do if we can't clean it up.
        }
    }
}

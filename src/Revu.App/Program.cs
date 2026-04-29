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

        // v2.16: ensure the Add/Remove Programs uninstall entry exists. Velopack
        // normally writes this during Setup.exe; this is a defensive top-up for
        // installs that ended up without it.
        try
        {
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            UninstallEntryRegistrar.EnsureRegistered(version);
        }
        catch
        {
            // best-effort; never fatal
        }

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
    // Velopack's Shortcuts API is marked obsolete because they now manage
    // shortcuts automatically — but our use case is the inverse, *removing*
    // legacy-named shortcuts (LoLReview.App.exe, LoLReview.exe) that exist
    // from the pre-rename install. The new auto-management doesn't help
    // there. Suppressing CS0618 locally until Velopack ships a non-obsolete
    // delete-by-name API.
#pragma warning disable CS0618
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
#pragma warning restore CS0618
}

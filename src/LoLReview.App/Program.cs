using System.Runtime.InteropServices;
using LoLReview.App.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using WinRT;

namespace LoLReview.App;

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
        // one named after packId (LoLReview.lnk) and one named after mainExe
        // (LoLReview.App.lnk). Remove the exe-named redundant one on install/update
        // so the user only sees a single shortcut. The packId shortcut stays.
        VelopackApp.Build()
            .OnAfterInstallFastCallback((v) => RemoveRedundantExeShortcut())
            .OnAfterUpdateFastCallback((v) => RemoveRedundantExeShortcut())
            .Run();

        try
        {
            AppDiagnostics.WriteVerbose("startup.log", "Program.Main starting");

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
    /// Deletes the redundant shortcut named after the main exe (LoLReview.App.lnk),
    /// leaving only the packId-named one (LoLReview.lnk). Best-effort; failures
    /// are silent because shortcut cleanup isn't critical to install success.
    /// </summary>
    private static void RemoveRedundantExeShortcut()
    {
        try
        {
            new Velopack.Windows.Shortcuts().DeleteShortcuts(
                "LoLReview.App.exe",
                Velopack.Windows.ShortcutLocation.Desktop
                | Velopack.Windows.ShortcutLocation.StartMenu);
        }
        catch
        {
            // Best-effort; nothing to do if we can't clean it up.
        }
    }
}

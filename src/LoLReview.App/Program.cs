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
        // Explicit shortcut creation ensures Desktop + Start Menu shortcuts always exist,
        // so the user never has to hunt for the exe after install/update.
        VelopackApp.Build()
            .OnAfterInstallFastCallback((v) =>
            {
                new Velopack.Windows.Shortcuts().CreateShortcutForThisExe(
                    Velopack.Windows.ShortcutLocation.Desktop |
                    Velopack.Windows.ShortcutLocation.StartMenu);
            })
            .OnAfterUpdateFastCallback((v) =>
            {
                new Velopack.Windows.Shortcuts().CreateShortcutForThisExe(
                    Velopack.Windows.ShortcutLocation.Desktop |
                    Velopack.Windows.ShortcutLocation.StartMenu);
            })
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
}

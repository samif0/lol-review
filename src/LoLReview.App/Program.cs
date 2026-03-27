using System.Runtime.InteropServices;
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

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoLReview", "startup.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        try
        {
            File.AppendAllText(logPath, $"\n[{DateTime.Now}] Program.Main starting\n");

            XamlCheckProcessRequirements();
            File.AppendAllText(logPath, $"[{DateTime.Now}] XamlCheckProcessRequirements passed\n");

            ComWrappersSupport.InitializeComWrappers();
            File.AppendAllText(logPath, $"[{DateTime.Now}] ComWrappers initialized\n");

            Application.Start(p =>
            {
                File.AppendAllText(logPath, $"[{DateTime.Now}] Application.Start callback\n");
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
                File.AppendAllText(logPath, $"[{DateTime.Now}] App created\n");
            });

            File.AppendAllText(logPath, $"[{DateTime.Now}] Application.Start returned (app closed)\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"[{DateTime.Now}] FATAL: {ex}\n");
        }
    }
}

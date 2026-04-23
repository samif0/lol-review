#nullable enable

using System.Runtime.InteropServices;
using LoLReview.App.Composition;
using LoLReview.App.Helpers;
using LoLReview.App.Startup;
using LoLReview.App.Views;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace LoLReview.App;

/// <summary>
/// Application entry point.
/// </summary>
public partial class App : Application
{
    private const double DefaultStartupWindowWidth = 1024;
    private const double DefaultStartupWindowHeight = 1080;
    private static IHost? _host;
    private static Window? _mainWindow;

    public static Window? MainWindow => _mainWindow;

    public App()
    {
        try
        {
            AppDiagnostics.WriteVerbose("startup.log", "App() constructor start");
            InitializeComponent();
            AppDiagnostics.WriteVerbose("startup.log", "InitializeComponent done");

            UnhandledException += (sender, args) =>
            {
                AppDiagnostics.WriteCrash(args.Exception);
                args.Handled = true;
            };
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteCrash(ex);
            throw;
        }
    }

    public static T GetService<T>() where T : class
    {
        if (_host?.Services.GetService(typeof(T)) is T service)
        {
            return service;
        }

        throw new InvalidOperationException(
            $"Service {typeof(T).FullName} is not registered in the DI container.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppDiagnostics.WriteVerbose("startup.log", "OnLaunched START");

        _mainWindow = new Window { Title = "Revu" };
        _mainWindow.CenterOnScreen(DefaultStartupWindowWidth, DefaultStartupWindowHeight);

        // Extend the shell content into the title bar area. We'll draw our own
        // HUD-styled bar in ShellPage/OnboardingPage; Windows still renders the
        // system min/max/close overlays on the right, so we leave room for them.
        _mainWindow.ExtendsContentIntoTitleBar = true;

        // Set the window icon explicitly so Windows' hover-preview thumbnail,
        // alt-tab, and task-switcher show the right icon. Two layers required
        // in unpackaged WinUI 3:
        //   1. AppWindow.SetIcon — handles alt-tab + hover preview reliably.
        //   2. Win32 WM_SETICON via SendMessage — forces the taskbar button
        //      icon to the same resource. Without this, the taskbar often
        //      keeps showing a stale cached icon even after AppWindow.SetIcon.
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "lolreview.ico");
            if (File.Exists(iconPath))
            {
                _mainWindow.AppWindow.SetIcon(iconPath);
                ApplyWin32Icon(_mainWindow, iconPath);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteVerbose("startup.log", $"Could not set window icon: {ex.Message}");
        }

        var manager = WinUIEx.WindowManager.Get(_mainWindow);
        manager.MinWidth = 1024;
        manager.MinHeight = 640;

        var loadingText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "Revu - Loading...",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var loadingGrid = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 20, 18, 30)) // #14121E card bg
        };
        loadingGrid.Children.Add(loadingText);

        _mainWindow.Content = loadingGrid;
        _mainWindow.Activate();

        _mainWindow.DispatcherQueue.TryEnqueue(async () => await LaunchAsync(loadingText));
    }

    private static async Task LaunchAsync(Microsoft.UI.Xaml.Controls.TextBlock loadingText)
    {
        try
        {
            _host = AppHostFactory.CreateHost();
            DispatcherHelper.Initialize();
            AppDiagnostics.WriteVerbose("startup.log", "Host created and dispatcher initialized");

            await GetService<IAppBootstrapper>().BootstrapAsync();
            AppDiagnostics.WriteVerbose("startup.log", "Bootstrap completed");

            // Onboarding gate: if the user hasn't signed up/in AND hasn't opted out,
            // show the onboarding page first. It swaps to the shell via its Completed event.
            var configService = GetService<LoLReview.Core.Services.IConfigService>();
            await configService.LoadAsync();

            await DispatcherHelper.RunOnUIThreadAsync(() =>
            {
                if (configService.OnboardingComplete)
                {
                    _mainWindow!.Content = new ShellPage();
                    AppDiagnostics.WriteVerbose("startup.log", "Onboarding complete -- ShellPage assigned");
                }
                else
                {
                    var onboarding = new Views.OnboardingPage();
                    onboarding.Completed += () =>
                    {
                        try
                        {
                            _mainWindow!.Content = new ShellPage();
                            AppDiagnostics.WriteVerbose("startup.log", "Onboarding completed -- ShellPage swapped in");
                        }
                        catch (Exception swapEx)
                        {
                            AppDiagnostics.WriteCrash(swapEx);
                        }
                    };
                    _mainWindow!.Content = onboarding;
                    AppDiagnostics.WriteVerbose("startup.log", "OnboardingPage assigned to main window");
                }
            });

            await _host.StartAsync();
            AppDiagnostics.WriteVerbose("startup.log", "Host started");
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteCrash(ex);

            try
            {
                await DispatcherHelper.RunOnUIThreadAsync(
                    () => loadingText.Text = $"Startup error:\n{ex.Message}");
            }
            catch (Exception uiException)
            {
                AppDiagnostics.WriteCrash(uiException);
            }
        }
    }

    // ── Win32 icon helper ──────────────────────────────────────────
    // WinUI 3's AppWindow.SetIcon handles alt-tab and hover-preview
    // reliably, but the taskbar button icon often stays stuck on the
    // cached exe icon. Sending WM_SETICON directly to the HWND forces
    // the taskbar to refresh. LoadImage with LR_LOADFROMFILE reads the
    // .ico straight off disk (no cache).

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void ApplyWin32Icon(Window window, string iconPath)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            var hIconSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            var hIconBig = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);

            if (hIconSmall != IntPtr.Zero)
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconSmall);
            if (hIconBig != IntPtr.Zero)
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIconBig);
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteVerbose("startup.log", $"ApplyWin32Icon failed: {ex.Message}");
        }
    }
}

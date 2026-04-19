#nullable enable

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

        _mainWindow = new Window { Title = "LoL Review" };
        _mainWindow.CenterOnScreen(DefaultStartupWindowWidth, DefaultStartupWindowHeight);

        var manager = WinUIEx.WindowManager.Get(_mainWindow);
        manager.MinWidth = 1024;
        manager.MinHeight = 640;

        var loadingText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "LoL Review - Loading...",
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

            await DispatcherHelper.RunOnUIThreadAsync(
                () => _mainWindow!.Content = new ShellPage());
            AppDiagnostics.WriteVerbose("startup.log", "ShellPage assigned to main window");

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
}

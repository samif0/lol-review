#nullable enable

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Activation;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.Services;
using LoLReview.App.ViewModels;
using LoLReview.App.Views;
using LoLReview.Core.Data;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Lcu;
using LoLReview.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace LoLReview.App;

/// <summary>
/// Application entry point. Configures DI, enforces single-instance, and bootstraps the shell.
/// </summary>
public partial class App : Application
{
    private static IHost? _host;
    private static Window? _mainWindow;
    public static Window? MainWindow => _mainWindow;
    private readonly SingleInstanceManager _singleInstance = new();

    public App()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoLReview", "startup.log");
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now}] App() constructor start\n");
            InitializeComponent();
            File.AppendAllText(logPath, $"[{DateTime.Now}] InitializeComponent done\n");

            UnhandledException += (s, e) =>
            {
                File.AppendAllText(logPath, $"[{DateTime.Now}] UnhandledException: {e.Exception}\n");
                e.Handled = true;
            };
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"[{DateTime.Now}] App() constructor FAILED: {ex}\n");
        }
    }

    /// <summary>
    /// Resolve a service from the DI container.
    /// </summary>
    public static T GetService<T>() where T : class
    {
        if (_host?.Services.GetService<T>() is T service)
        {
            return service;
        }

        throw new InvalidOperationException(
            $"Service {typeof(T).FullName} is not registered in the DI container.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoLReview", "startup.log");
        File.AppendAllText(logPath, $"[{DateTime.Now}] OnLaunched START\n");

        // Show window immediately
        _mainWindow = new Window { Title = "LoL Review" };
        _mainWindow.SetWindowSize(1100, 700);

        var loadingText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "LoL Review — Loading...",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var loadingGrid = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 10, 10, 15))
        };
        loadingGrid.Children.Add(loadingText);
        _mainWindow.Content = loadingGrid;
        _mainWindow.Activate();

        // Initialize on dispatcher
        _mainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                _host = CreateHost();
                DispatcherHelper.Initialize();

                var dbInit = GetService<DatabaseInitializer>();
                await dbInit.InitializeAsync();

                // Load custom app theme (overrides some WinUI defaults with app colors)
                try
                {
                    Current.Resources.MergedDictionaries.Add(
                        new Microsoft.UI.Xaml.ResourceDictionary { Source = new Uri("ms-appx:///Themes/AppTheme.xaml") });
                }
                catch (Exception themeEx)
                {
                    File.AppendAllText(logPath, $"[{DateTime.Now}] AppTheme load failed: {themeEx.Message}\n");
                }

                var shell = new ShellPage();
                _mainWindow.Content = shell;

                await _host.StartAsync();
            }
            catch (Exception ex)
            {
                loadingText.Text = $"Startup error:\n{ex.Message}";
                var crashPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoLReview", "crash.log");
                File.WriteAllText(crashPath, $"{DateTime.Now}\n{ex}");
            }
        });
    }

    private static IHost CreateHost()
    {
        return new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddDebug();
            })
            .ConfigureServices((_, services) =>
            {
                // ── Data layer ─────────────────────────────────────
                services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
                services.AddSingleton<DatabaseInitializer>();

                // ── Repositories ───────────────────────────────────
                services.AddSingleton<IGameRepository, GameRepository>();
                services.AddSingleton<IGameEventsRepository, GameEventsRepository>();
                services.AddSingleton<IObjectivesRepository, ObjectivesRepository>();
                services.AddSingleton<IRulesRepository, RulesRepository>();
                services.AddSingleton<IConceptTagRepository, ConceptTagRepository>();
                services.AddSingleton<IVodRepository, VodRepository>();
                services.AddSingleton<ISessionLogRepository, SessionLogRepository>();
                services.AddSingleton<IDerivedEventsRepository, DerivedEventsRepository>();
                services.AddSingleton<IMatchupNotesRepository, MatchupNotesRepository>();
                services.AddSingleton<INotesRepository, NotesRepository>();
                services.AddSingleton<IPromptsRepository, PromptsRepository>();
                services.AddSingleton<ITiltCheckRepository, TiltCheckRepository>();

                // ── Domain services ───────────────────────────────
                services.AddSingleton<IConfigService, ConfigService>();
                services.AddSingleton<IClipService, ClipService>();
                services.AddSingleton<IAnalysisService, AnalysisService>();

                // ── Messaging ──────────────────────────────────────
                services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

                // ── LCU services ───────────────────────────────────
                services.AddSingleton<ILcuCredentialDiscovery, LcuCredentialDiscovery>();

                // LCU HttpClient (bypasses SSL for self-signed League cert)
                services.AddHttpClient<ILcuClient, LcuClient>("LcuClient")
                    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = BypassSslValidation
                    });

                // Live Event API HttpClient (bypasses SSL for self-signed cert)
                services.AddHttpClient<ILiveEventApi, LiveEventApi>("LiveEventApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = BypassSslValidation
                    });

                // Game monitor — polls League client for game phase transitions
                services.AddHostedService<GameMonitorService>();

                // ── UI services ────────────────────────────────────
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<ISystemTrayService, SystemTrayService>();
                services.AddSingleton<IUpdateService, UpdateService>();

                // ── ViewModels (transient — new instance per resolve) ─
                services.AddTransient<ShellViewModel>();
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<SessionLoggerViewModel>();
                services.AddTransient<ObjectivesViewModel>();
                services.AddTransient<RulesViewModel>();
                services.AddTransient<TiltCheckViewModel>();
                services.AddTransient<HistoryViewModel>();
                services.AddTransient<LossesViewModel>();
                services.AddTransient<AnalyticsViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<ReviewViewModel>();
                services.AddTransient<PreGameDialogViewModel>();
                services.AddTransient<ManualEntryDialogViewModel>();
                services.AddTransient<GameReviewDialogViewModel>();
                services.AddTransient<VodPlayerViewModel>();
            })
            .Build();
    }

    /// <summary>
    /// SSL certificate validation callback that accepts any certificate.
    /// Required because the League Client uses self-signed certificates.
    /// </summary>
    private static bool BypassSslValidation(
        HttpRequestMessage message,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors) => true;
}

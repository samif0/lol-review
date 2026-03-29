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
        try
        {
            AppDiagnostics.WriteVerbose("startup.log", "App() constructor start");
            InitializeComponent();
            AppDiagnostics.WriteVerbose("startup.log", "InitializeComponent done");

            UnhandledException += (s, e) =>
            {
                AppDiagnostics.WriteCrash(e.Exception);
                e.Handled = true;
            };
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteCrash(ex);
            throw;
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
        AppDiagnostics.WriteVerbose("startup.log", "OnLaunched START");

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

                var legacyMigrator = GetService<LegacyDatabaseMigrationService>();
                var migratedFrom = legacyMigrator.TryMigrate();
                if (migratedFrom is not null)
                {
                    AppDiagnostics.WriteVerbose("startup.log", $"Migrated legacy DB from: {migratedFrom}");
                }

                // 1. Pre-flight integrity check — logs DB path/size/game count,
                //    refuses to proceed if DB was wiped but backups exist
                var integrityChecker = GetService<DatabaseIntegrityChecker>();
                integrityChecker.RunPreFlightChecks();

                // 2. Safety backup — always-on, no config needed, keeps last 3
                var backupService = GetService<IBackupService>();
                await backupService.CreateSafetyBackupAsync("pre-migration startup");

                // 3. User-configured backup (if enabled in settings)
                await backupService.RunBackupAsync();

                // 4. Schema init — CREATE TABLE IF NOT EXISTS, ALTER TABLE migrations, seed data
                var dbInit = GetService<DatabaseInitializer>();
                await dbInit.InitializeAsync();

                // 5. Diagnostic: log DB path and game count to file for debugging
                try
                {
                    var connFactory = GetService<IDbConnectionFactory>();
                    using var diagConn = connFactory.CreateConnection();
                    using var diagCmd = diagConn.CreateCommand();
                    diagCmd.CommandText = "SELECT COUNT(*) FROM games";
                    var gameCount = diagCmd.ExecuteScalar();
                    AppDiagnostics.WriteVerbose(
                        "startup.log",
                        $"DB path: {connFactory.DatabasePath}; " +
                        $"DB game count: {gameCount}; " +
                        $"DB file size: {new FileInfo(connFactory.DatabasePath).Length} bytes");
                }
                catch (Exception diagEx)
                {
                    AppDiagnostics.WriteVerbose("startup.log", $"DB diagnostic failed: {diagEx.Message}");
                }

                // Load XamlControlsResources at runtime (not in App.xaml) to avoid heap corruption
                try
                {
                    Current.Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
                }
                catch (Exception xcrEx)
                {
                    AppDiagnostics.WriteVerbose("startup.log", $"XamlControlsResources load failed: {xcrEx.Message}");
                }

                // Load custom app theme (overrides some WinUI defaults with app colors)
                try
                {
                    Current.Resources.MergedDictionaries.Add(
                        new Microsoft.UI.Xaml.ResourceDictionary { Source = new Uri("ms-appx:///Themes/AppTheme.xaml") });
                }
                catch (Exception themeEx)
                {
                    AppDiagnostics.WriteVerbose("startup.log", $"AppTheme load failed: {themeEx.Message}");
                }

                var shell = new ShellPage();
                _mainWindow.Content = shell;

                await _host.StartAsync();
            }
            catch (Exception ex)
            {
                loadingText.Text = $"Startup error:\n{ex.Message}";
                AppDiagnostics.WriteCrash(ex);
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
                services.AddSingleton<LegacyDatabaseMigrationService>();
                services.AddSingleton<DatabaseIntegrityChecker>();
                services.AddSingleton<DatabaseInitializer>();
                services.AddSingleton<IBackupService, BackupService>();

                // ── Repositories ───────────────────────────────────
                services.AddSingleton<IGameRepository, GameRepository>();
                services.AddSingleton<IGameEventsRepository, GameEventsRepository>();
                services.AddSingleton<IObjectivesRepository, ObjectivesRepository>();
                services.AddSingleton<IRulesRepository, RulesRepository>();
                services.AddSingleton<IConceptTagRepository, ConceptTagRepository>();
                services.AddSingleton<IVodRepository, VodRepository>();
                services.AddSingleton<ISessionLogRepository, SessionLogRepository>();
                services.AddSingleton<IReviewDraftRepository, ReviewDraftRepository>();
                services.AddSingleton<IMissedGameDecisionRepository, MissedGameDecisionRepository>();
                services.AddSingleton<IDerivedEventsRepository, DerivedEventsRepository>();
                services.AddSingleton<IMatchupNotesRepository, MatchupNotesRepository>();
                services.AddSingleton<INotesRepository, NotesRepository>();
                services.AddSingleton<IPromptsRepository, PromptsRepository>();
                services.AddSingleton<ITiltCheckRepository, TiltCheckRepository>();

                // ── Domain services ───────────────────────────────
                services.AddSingleton<IConfigService, ConfigService>();
                services.AddSingleton<IClipService, ClipService>();
                services.AddSingleton<IAnalysisService, AnalysisService>();
                services.AddSingleton<IVodService, VodService>();
                services.AddSingleton<IGameService, GameService>();
                services.AddSingleton<ICoachSidecarClient, CoachSidecarClient>();
                services.AddSingleton<ICoachRecommendationService, CoachRecommendationService>();
                services.AddSingleton<ICoachTrainingService, CoachTrainingService>();
                services.AddSingleton<ICoachLabService, CoachLabService>();

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
                services.AddTransient<AnalyticsViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<ReviewViewModel>();
                services.AddTransient<PreGameDialogViewModel>();
                services.AddTransient<ManualEntryDialogViewModel>();
                services.AddTransient<GameReviewDialogViewModel>();
                services.AddTransient<VodPlayerViewModel>();
                services.AddTransient<CoachLabViewModel>();
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

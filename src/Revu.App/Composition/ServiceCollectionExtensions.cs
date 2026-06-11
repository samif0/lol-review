#nullable enable

using System;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Services;
using Revu.App.Startup;
using Revu.App.ViewModels;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Revu.App.Composition;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDataInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<LegacyDatabaseMigrationService>();
        services.AddSingleton<DatabaseIntegrityChecker>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IBackupService, BackupService>();
        return services;
    }

    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddSingleton<GameRepository>();
        services.AddSingleton<IGameRepository>(sp => sp.GetRequiredService<GameRepository>());
        services.AddSingleton<IGameHistoryQuery>(sp => sp.GetRequiredService<GameRepository>());
        services.AddSingleton<IGameAnalyticsQuery>(sp => sp.GetRequiredService<GameRepository>());
        services.AddSingleton<IGameDeletionService>(sp => sp.GetRequiredService<GameRepository>());
        services.AddSingleton<IGameEventsRepository, GameEventsRepository>();
        services.AddSingleton<IObjectivesRepository, ObjectivesRepository>();
        services.AddSingleton<IRulesRepository, RulesRepository>();
        services.AddSingleton<IConceptTagRepository, ConceptTagRepository>();
        services.AddSingleton<IVodRepository, VodRepository>();
        services.AddSingleton<ISessionLogRepository, SessionLogRepository>();
        services.AddSingleton<IReviewDraftRepository, ReviewDraftRepository>();
        services.AddSingleton<IMissedGameDecisionRepository, MissedGameDecisionRepository>();
        services.AddSingleton<IDerivedEventsRepository, DerivedEventsRepository>();
        services.AddSingleton<IEvidenceRepository, EvidenceRepository>();
        services.AddSingleton<IMatchupNotesRepository, MatchupNotesRepository>();
        services.AddSingleton<INotesRepository, NotesRepository>();
        services.AddSingleton<IPromptsRepository, PromptsRepository>();
        services.AddSingleton<ITiltCheckRepository, TiltCheckRepository>();
        return services;
    }

    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IProtectedSecretStore, ProtectedSecretStore>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IClipService, ClipService>();
        services.AddSingleton<IAnalysisService, AnalysisService>();
        services.AddSingleton<IVodService, VodService>();
        services.AddSingleton<IGameService, GameService>();
        services.AddSingleton<IReviewWorkflowService, ReviewWorkflowService>();
        services.AddSingleton<IReviewExportService, ReviewExportService>();
        services.AddSingleton<IGameLifecycleWorkflowService, GameLifecycleWorkflowService>();

        // Riot proxy / auth (Path B). Explicit timeouts so a stalled upstream
        // can't hang a request for the default 100s (the loopback LCU/Live
        // clients already set their own short timeouts).
        services.AddHttpClient<IRiotAuthClient, RiotAuthClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IRiotMatchClient, RiotMatchClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<EnemyLanerBackfillService>();

        // Public clip sharing (revu.lol/<id>). Uploads a local clip via the
        // logged-in session token; the clip is publicly viewable, owner-deletable.
        // Larger timeout — a clip body can be up to ~100 MB.
        services.AddHttpClient<IClipUploadService, ClipUploadService>(c => c.Timeout = TimeSpan.FromMinutes(5));

        // v2.16.1: pre-game intel rotator data sources.
        services.AddHttpClient<IRiotChampionDataClient, RiotChampionDataClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<PreGameIntelService>();

        // v2.16.1: minimize window + suspend animations while a game is running.
        services.AddSingleton<Revu.App.Services.InGameBackgroundOrchestrator>();

        // Coach feature removed for v1. The seam consumers (ReviewWorkflowService,
        // GameLifecycleWorkflowService, VodPlayerViewModel) resolve to this no-op.
        services.AddSingleton<ICoachSidecarNotifier, NullCoachSidecarNotifier>();
        return services;
    }

    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        return services;
    }

    public static IServiceCollection AddLcuServices(
        this IServiceCollection services,
        Func<HttpRequestMessage, X509Certificate2?, X509Chain?, System.Net.Security.SslPolicyErrors, bool> bypassSslValidation)
    {
        services.AddSingleton<ILcuCredentialDiscovery, LcuCredentialDiscovery>();
        services.AddSingleton<IGameEndCaptureService, GameEndCaptureService>();
        services.AddSingleton<IMatchHistoryReconciliationService, MatchHistoryReconciliationService>();

        services.AddHttpClient("LcuClient")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = bypassSslValidation
            });

        services.AddSingleton<ILcuClient>(sp =>
            new LcuClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("LcuClient"),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LcuClient>>()));

        services.AddHttpClient<ILiveEventApi, LiveEventApi>("LiveEventApi")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = bypassSslValidation
            });

        services.AddSingleton<GameMonitorService>();
        services.AddSingleton<IGameMonitorService>(sp => sp.GetRequiredService<GameMonitorService>());
        services.AddHostedService(sp => sp.GetRequiredService<GameMonitorService>());
        return services;
    }

    public static IServiceCollection AddUiServices(this IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ISystemTrayService, SystemTrayService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        return services;
    }

    public static IServiceCollection AddViewModels(this IServiceCollection services)
    {
        services.AddTransient<ShellViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<GamesViewModel>();
        services.AddTransient<SessionLoggerViewModel>();
        services.AddTransient<ObjectivesViewModel>();
        services.AddTransient<RulesViewModel>();
        services.AddTransient<TiltCheckViewModel>();
        services.AddTransient<PatternReviewViewModel>();
        services.AddTransient<AnalyticsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ReviewViewModel>();
        services.AddTransient<PreGameDialogViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<LoginDialogViewModel>();
        services.AddTransient<ManualEntryDialogViewModel>();
        services.AddTransient<GameReviewDialogViewModel>();
        services.AddTransient<VodPlayerViewModel>();
        services.AddTransient<ObjectiveGamesViewModel>();
        services.AddTransient<ObjectiveNotesViewModel>();
        return services;
    }

    public static IServiceCollection AddStartupPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IAppBootstrapper, AppBootstrapper>();
        services.AddSingleton<IStartupTask, LegacyDatabaseMigrationStartupTask>();
        services.AddSingleton<IStartupTask, DatabaseSafetyStartupTask>();
        services.AddSingleton<IStartupTask, DatabaseInitializationStartupTask>();
        services.AddSingleton<IStartupTask, AppResourcesStartupTask>();
        return services;
    }
}

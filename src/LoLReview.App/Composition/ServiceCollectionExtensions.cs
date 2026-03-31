#nullable enable

using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Contracts;
using LoLReview.App.Services;
using LoLReview.App.Startup;
using LoLReview.App.ViewModels;
using LoLReview.Core.Data;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Lcu;
using LoLReview.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LoLReview.App.Composition;

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
        return services;
    }

    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IClipService, ClipService>();
        services.AddSingleton<IAnalysisService, AnalysisService>();
        services.AddSingleton<IVodService, VodService>();
        services.AddSingleton<IGameService, GameService>();
        services.AddSingleton<IReviewWorkflowService, ReviewWorkflowService>();
        services.AddSingleton<IGameLifecycleWorkflowService, GameLifecycleWorkflowService>();
        services.AddSingleton<ICoachSidecarClient, CoachSidecarClient>();
        services.AddSingleton<ICoachRecommendationService, CoachRecommendationService>();
        services.AddSingleton<ICoachTrainingService, CoachTrainingService>();
        services.AddSingleton<ICoachLabService, CoachLabService>();
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

        services.AddHostedService<GameMonitorService>();
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
        services.AddTransient<ObjectiveGamesViewModel>();
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

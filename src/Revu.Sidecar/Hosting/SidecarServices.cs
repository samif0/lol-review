#nullable enable

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.Messaging;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static class SidecarServices
{
    public static IServiceCollection AddSidecarServices(this IServiceCollection services, bool isolatedHostTest)
    {
        // Read queries use the physically read-only database graph.
        services.AddSingleton<IDbConnectionFactory, ReadOnlySqliteConnectionFactory>();

        services.AddSingleton<IProtectedSecretStore, ProtectedSecretStore>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IBackupService, BackupService>();

        services.AddSingleton<GameRepository>();
        services.AddSingleton<IGameHistoryQuery>(sp => sp.GetRequiredService<GameRepository>());
        services.AddSingleton<IGameRepository>(sp => sp.GetRequiredService<GameRepository>());
        services.AddSingleton<IObjectivesRepository, ObjectivesRepository>();
        services.AddSingleton<IPromptsRepository, PromptsRepository>();
        services.AddSingleton<IDeathClassificationsRepository, DeathClassificationsRepository>();
        services.AddSingleton<IRulesRepository, RulesRepository>();
        services.AddSingleton<IHardStopsRepository, HardStopsRepository>();
        services.AddSingleton<IVodRepository, VodRepository>();
        services.AddSingleton<ISessionLogRepository, SessionLogRepository>();
        services.AddSingleton<ICoachingStintsRepository, CoachingStintsRepository>();
        services.AddSingleton<IEvidenceRepository, EvidenceRepository>();
        services.AddSingleton<IDerivedEventsRepository, DerivedEventsRepository>();
        services.AddSingleton<IGameEventsRepository, GameEventsRepository>();
        services.AddSingleton<IEventCorrectionsRepository, EventCorrectionsRepository>();
        services.AddSingleton<IMatchupNotesRepository, MatchupNotesRepository>();
        services.AddSingleton<IMatchupsRepository, MatchupsRepository>();
        services.AddSingleton<IReviewDraftRepository, ReviewDraftRepository>();
        services.AddSingleton<ITiltCheckRepository, TiltCheckRepository>();

        services.AddSingleton<IGameAnalyticsQuery>(sp => sp.GetRequiredService<GameRepository>());

        services.AddSingleton<IConceptTagRepository, ConceptTagRepository>();
        services.AddSingleton<IAnalysisService, AnalysisService>();

        // Page snapshots share the read graph.
        services.AddSingleton<DashboardSnapshotBuilder>();
        services.AddSingleton<VodSnapshotBuilder>();
        services.AddSingleton<GamesSnapshotBuilder>();
        services.AddSingleton<ObjectivesSnapshotBuilder>();
        services.AddSingleton<ObjectiveGamesSnapshotBuilder>();
        services.AddSingleton<ObjectiveNotesSnapshotBuilder>();
        services.AddSingleton<ObjectiveEditSnapshotBuilder>();
        services.AddSingleton<ReviewSnapshotBuilder>();
        services.AddSingleton<RulesSnapshotBuilder>();
        services.AddSingleton<TiltCheckSnapshotBuilder>();
        services.AddSingleton<MatchupsSnapshotBuilder>();
        services.AddSingleton<PatternsSnapshotBuilder>();
        services.AddSingleton<ConfigSnapshotBuilder>();
        services.AddSingleton<IClipService, ClipService>();
        services.AddSingleton<SettingsStatusSnapshotBuilder>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IReviewExportService, ReviewExportService>();

        // Mutations resolve through a separate write-capable service provider.
        services.AddSingleton<WriteServices>();
        services.AddSingleton(sp => new RecordingRegistrationService(
            AppDataPaths.RecordingsDirectory,
            sp.GetRequiredService<WriteServices>().RecordingLinks,
            sp.GetRequiredService<WriteServices>().BackupGuard.EnsureBackedUpAsync,
            sp.GetRequiredService<ILogger<RecordingRegistrationService>>(),
            gameId => sp.GetRequiredService<SidecarEventHub>().Publish("vodLinked", new { gameId })));

        // LCU detection and desktop event delivery.
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        services.AddSingleton<LcuLiveState>();
        services.AddSingleton<SidecarEventHub>();

        services.AddSingleton<ILcuCredentialDiscovery, LcuCredentialDiscovery>();
        services.AddSingleton<IGameEndCaptureService, GameEndCaptureService>();
        services.AddSingleton<IMissedGameDecisionRepository, MissedGameDecisionRepository>();
        services.AddSingleton<IMatchHistoryReconciliationService, MatchHistoryReconciliationService>();

        services.AddHttpClient("LcuClient")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { ServerCertificateCustomValidationCallback = BypassLcuSsl });
        services.AddSingleton<ILcuClient>(sp => new LcuClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("LcuClient"),
            sp.GetRequiredService<ILogger<LcuClient>>()));
        services.AddHttpClient<ILiveEventApi, LiveEventApi>("LiveEventApi")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { ServerCertificateCustomValidationCallback = BypassLcuSsl });

        // Hosted services stop in reverse order: coordinator, monitor, then accepted work.
        services.AddSingleton<SidecarBackgroundWork>();
        services.AddHostedService(sp => sp.GetRequiredService<SidecarBackgroundWork>());
        services.AddSingleton<GameMonitorService>();
        services.AddSingleton<PracticeCaptureService>();
        services.AddSingleton<IGameMonitorService>(sp => sp.GetRequiredService<GameMonitorService>());
        if (!isolatedHostTest) services.AddHostedService(sp => sp.GetRequiredService<GameMonitorService>());

        services.AddSingleton<HardStopEnforcer>(sp =>
        {
            var write = sp.GetRequiredService<WriteServices>();
            return new HardStopEnforcer(
                write.Rules,
                write.Games,
                write.HardStops,
                sp.GetRequiredService<ILcuClient>(),
                sp.GetRequiredService<SidecarEventHub>(),
                sp.GetRequiredService<LcuLiveState>(),
                sp.GetRequiredService<ILogger<HardStopEnforcer>>());
        });
        services.AddSingleton<SidecarGameFlowCoordinator>();
        if (!isolatedHostTest) services.AddHostedService(sp => sp.GetRequiredService<SidecarGameFlowCoordinator>());

        services.AddHttpClient<IRiotChampionDataClient, RiotChampionDataClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<PreGameIntelService>();
        services.AddSingleton<PreGameSnapshotBuilder>();
        return services;
    }

    private static bool BypassLcuSsl(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
        => errors == SslPolicyErrors.None || request.RequestUri?.IsLoopback == true;
}

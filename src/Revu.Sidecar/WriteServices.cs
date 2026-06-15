#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Isolated dependency graph for the sidecar's WRITE endpoints.
///
/// <para>
/// Reads and writes are physically separated: the main app DI container uses the
/// <see cref="ReadOnlySqliteConnectionFactory"/>, so no read path can ever write.
/// This container is built ONLY with the <see cref="WriteSqliteConnectionFactory"/>
/// and holds only the Revu.Core write repositories/services the daily-loop actions
/// need. The write endpoints resolve services from here.
/// </para>
///
/// <para>
/// We reuse Revu.Core's tested write methods verbatim (the same code the WinUI app
/// runs) rather than hand-rolling SQL. The sidecar NEVER runs DatabaseInitializer —
/// schema ownership stays with the WinUI app.
/// </para>
/// </summary>
public sealed class WriteServices : IDisposable
{
    private readonly ServiceProvider _provider;

    public WriteServices(ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // Write-capable DB factory (ReadWrite, no create, no migrate).
        services.AddSingleton<IDbConnectionFactory, WriteSqliteConnectionFactory>();

        // Core dependencies the write repos/services need (mirrors the read graph
        // in Program.cs, but bound to the WRITE factory).
        services.AddSingleton<IProtectedSecretStore, ProtectedSecretStore>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IBackupService, BackupService>();

        // Repositories the daily-loop writes need. GameRepository implements
        // several interfaces; register the concrete once and expose the slices.
        services.AddSingleton<GameRepository>();
        services.AddSingleton<IGameRepository>(sp => sp.GetRequiredService<GameRepository>());
        services.AddSingleton<IGameHistoryQuery>(sp => sp.GetRequiredService<GameRepository>());
        services.AddSingleton<ISessionLogRepository, SessionLogRepository>();
        services.AddSingleton<IObjectivesRepository, ObjectivesRepository>();
        // Rules CRUD (create / update / toggle / delete). Reuses the same
        // schema-tolerant RulesRepository the WinUI app writes through, bound to
        // the WRITE factory here. The read graph (Program.cs) has its own
        // read-only IRulesRepository for the GET /api/rules snapshot.
        services.AddSingleton<IRulesRepository, RulesRepository>();
        services.AddSingleton<ITiltCheckRepository, TiltCheckRepository>();
        services.AddSingleton<IMatchupNotesRepository, MatchupNotesRepository>();
        services.AddSingleton<IConceptTagRepository, ConceptTagRepository>();
        services.AddSingleton<IReviewDraftRepository, ReviewDraftRepository>();
        services.AddSingleton<IEvidenceRepository, EvidenceRepository>();
        services.AddSingleton<IVodRepository, VodRepository>();
        // Review-page write slices (Batch 2): per-death cause classification,
        // per-objective custom-prompt answers. (Evidence triage reuses the
        // IEvidenceRepository above; focus-adherence reuses ISessionLogRepository;
        // objective practiced-flag side-effects reuse IObjectivesRepository.)
        services.AddSingleton<IDeathClassificationsRepository, DeathClassificationsRepository>();
        services.AddSingleton<IPromptsRepository, PromptsRepository>();

        // Review-save's deeper graph: IReviewWorkflowService.SaveAsync needs
        // IVodService + ICoachSidecarNotifier too. VodService has its own deps;
        // the coach notifier is the null no-op (same as WinUI ships).
        services.AddSingleton<IClipService, ClipService>();
        services.AddSingleton<IVodService, VodService>();
        services.AddSingleton<ICoachSidecarNotifier, NullCoachSidecarNotifier>();

        // The review save — the big transactional multi-table write.
        services.AddSingleton<IReviewWorkflowService, ReviewWorkflowService>();

        // ── Live game-end capture (Batch 5 / LCU) ─────────────────────────────
        // The hosted GameMonitorService captures end-of-game stats and fires a
        // GameEndedMessage; the SidecarGameFlowCoordinator runs the SAME game-save
        // workflow the WinUI ShellViewModel did, so live-captured games keep
        // recording. IGameService persists the game row + session log + derived
        // events + VOD match; IGameLifecycleWorkflowService layers practiced-
        // objective recording + criteria evaluation on top. Extra deps it needs:
        //   • IGameEventsRepository / IDerivedEventsRepository — GameService writes
        //     the kill-feed + derived-event rows for the captured game.
        //   • IMissedGameDecisionRepository — reconcile dismissals.
        services.AddSingleton<IGameEventsRepository, GameEventsRepository>();
        services.AddSingleton<IDerivedEventsRepository, DerivedEventsRepository>();
        services.AddSingleton<IMissedGameDecisionRepository, MissedGameDecisionRepository>();
        services.AddSingleton<IGameService, GameService>();
        services.AddSingleton<IGameLifecycleWorkflowService, GameLifecycleWorkflowService>();

        // ── Riot auth / account / clip-share / backfill (Batch 4) ─────────────
        // SECURITY: the session token persists via IConfigService -> the DPAPI
        // IProtectedSecretStore registered above (write-graph). These HTTP clients
        // talk to the Cloudflare proxy (RiotProxyEndpoint.BaseUrl); the proxy
        // injects the server-side Riot key. Timeouts mirror the WinUI app's
        // ServiceCollectionExtensions registration verbatim so a hung request can't
        // wedge the sidecar (30s auth/match, 5min clip upload — a body can be large).
        services.AddHttpClient<IRiotAuthClient, RiotAuthClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IRiotMatchClient, RiotMatchClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IClipUploadService, ClipUploadService>(c => c.Timeout = TimeSpan.FromMinutes(5));
        // Backfill services walk games missing enemy_laner / laning@10 and resolve
        // them via Match-V5 (through IRiotMatchClient). Both write to GameRepository.
        services.AddSingleton<EnemyLanerBackfillService>();
        services.AddSingleton<LaningBackfillService>();

        // One-time safety backup guard (first write of a session).
        services.AddSingleton<SessionBackupGuard>();

        _provider = services.BuildServiceProvider();
    }

    public ISessionLogRepository SessionLog => _provider.GetRequiredService<ISessionLogRepository>();
    public IObjectivesRepository Objectives => _provider.GetRequiredService<IObjectivesRepository>();
    public ITiltCheckRepository TiltChecks => _provider.GetRequiredService<ITiltCheckRepository>();
    public IReviewWorkflowService ReviewWorkflow => _provider.GetRequiredService<IReviewWorkflowService>();
    public IBackupService Backup => _provider.GetRequiredService<IBackupService>();
    // App config read-modify-write (POST /api/config/save). Reuses the WRITE-graph
    // ConfigService so it mutates the same config.json the WinUI app owns; never
    // touches secrets beyond what IConfigService.SaveAsync already round-trips.
    public IConfigService Config => _provider.GetRequiredService<IConfigService>();
    // Game deletion (POST /api/game/delete). GameRepository.DeleteAsync cascades
    // across child tables in a transaction and snapshots a DB backup first.
    public IGameRepository Games => _provider.GetRequiredService<IGameRepository>();
    // Rules CRUD (POST /api/rule/create, /update, /toggle, /delete). Reuses the
    // schema-tolerant RulesRepository write methods verbatim. Delete is a hard
    // DELETE FROM rules — the frontend confirms before calling.
    public IRulesRepository Rules => _provider.GetRequiredService<IRulesRepository>();

    // ── Review-page write slices (Batch 2) ───────────────────────────────────
    // Shared evidence triage (Review + VOD both POST these): polarity, objective
    // attach, status (dismiss/evidence/highlight). Reused verbatim from the WinUI
    // app's IEvidenceRepository.
    public IEvidenceRepository Evidence => _provider.GetRequiredService<IEvidenceRepository>();
    // VOD bookmark CRUD (POST /api/bookmark/add, /note, /delete, /objective, /tag,
    // /quality). VodRepository is already registered above (it's a
    // ReviewWorkflowService dependency); this exposes the write slice for the VOD
    // player's Quick Bookmark + bookmark-list edit actions. Clips/ffmpeg are NOT
    // here (deferred Batch 3) — these are plain note-bookmark + tagging writes.
    public IVodRepository Vod => _provider.GetRequiredService<IVodRepository>();
    // Per-death cause classification (POST /api/death/classify, /api/death/clear).
    public IDeathClassificationsRepository DeathClassifications => _provider.GetRequiredService<IDeathClassificationsRepository>();
    // Per-objective custom-prompt answers (POST /api/prompt/answer/save).
    public IPromptsRepository Prompts => _provider.GetRequiredService<IPromptsRepository>();
    // Per-game focus-adherence (POST /api/focus-adherence) reuses the SessionLog
    // property above; objective practiced-flag side-effects on evidence-attach
    // reuse the Objectives property above.

    // ── Clip extraction (Batch 3) ─────────────────────────────────────────────
    // ffmpeg clip extraction (POST /api/clip/extract, POST /api/pattern/moment/note).
    // ClipService is already registered above (it's a VodService dependency); this
    // exposes the write slice. ExtractClipAsync writes the .mp4 to ClipsFolder, then
    // the endpoint upserts the bookmark + evidence row — mirroring the WinUI VOD
    // player's ExtractClipCommand verbatim. Its ctor needs IConfigService + logger
    // (the read-the-ffmpeg-path + folder-size deps), both registered above.
    public IClipService Clips => _provider.GetRequiredService<IClipService>();
    // App-config READ (ClipsFolder for the clip output dir). The Config property
    // above is the same singleton; alias kept explicit for the clip endpoints.

    // ── Settings VOD scan (Batch 6) ───────────────────────────────────────────
    // POST /api/settings/scan-vods. IVodService.AutoMatchRecordingsAsync writes
    // newly-matched VOD links into the DB (a write — hence WRITE graph); the scan
    // diagnostics (FindRecordingsAsync + IVodRepository.GetAllVodsAsync) are reads
    // on the same service. VodService is already registered above (it's a
    // ReviewWorkflowService dependency); this exposes the scan slice. Mirrors the
    // SettingsPage code-behind OnScanVodsClick verbatim.
    public IVodService VodScan => _provider.GetRequiredService<IVodService>();

    // ── Riot auth / account / clip-share / backfill (Batch 4) ─────────────────
    // SECURITY-SENSITIVE: these endpoints persist the session token (DPAPI via
    // IConfigService/IProtectedSecretStore) and upload user clips. They MUST run
    // through THIS write container (the read-graph in Program.cs is read-only and
    // can't save config). Reused verbatim from Revu.Core — never hand-rolled.
    //
    // POST /api/auth/login|verify|signup|logout|resolve, GET /api/auth/status.
    public IRiotAuthClient RiotAuth => _provider.GetRequiredService<IRiotAuthClient>();
    // POST /api/clip/upload — IClipUploadService.UploadAsync(clipPath, token, ...)
    // returns the public revu.lol URL, then SetBookmarkShareUrlAsync persists it.
    public IClipUploadService ClipUpload => _provider.GetRequiredService<IClipUploadService>();
    // POST /api/backfill/start — enemy-laner + laning@10 resolution via Match-V5.
    public EnemyLanerBackfillService EnemyLanerBackfill => _provider.GetRequiredService<EnemyLanerBackfillService>();
    public LaningBackfillService LaningBackfill => _provider.GetRequiredService<LaningBackfillService>();

    // ── Live game-end capture (Batch 5 / LCU) ─────────────────────────────────
    // The end-of-game persistence the SidecarGameFlowCoordinator runs when the
    // hosted GameMonitorService fires GameEndedMessage. ProcessGameEndAsync saves
    // the game row + session_log + derived events + VOD match, then records the
    // pre-game practiced objectives and evaluates structured criteria — the exact
    // workflow the WinUI ShellViewModel invoked. Bound to the WRITE graph.
    public IGameLifecycleWorkflowService GameLifecycle => _provider.GetRequiredService<IGameLifecycleWorkflowService>();

    public SessionBackupGuard BackupGuard => _provider.GetRequiredService<SessionBackupGuard>();

    public void Dispose() => _provider.Dispose();
}

/// <summary>
/// Ensures a safety backup is taken before the FIRST write of a sidecar session —
/// belt-and-suspenders on top of Revu.Core's own pre-destructive-op backups.
/// Idempotent: subsequent writes in the same session skip it.
/// </summary>
public sealed class SessionBackupGuard
{
    private readonly IBackupService _backup;
    private readonly ILogger<SessionBackupGuard> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public SessionBackupGuard(IBackupService backup, ILogger<SessionBackupGuard> logger)
    {
        _backup = backup;
        _logger = logger;
    }

    /// <summary>
    /// Take a one-time safety backup before the first write. Failure to back up
    /// is logged but does NOT block the write (the named pre-tauri snapshot and
    /// Revu.Core's own safety backups remain the primary nets); however we prefer
    /// to surface it.
    /// </summary>
    public async Task EnsureBackedUpAsync()
    {
        if (_done) return;
        await _gate.WaitAsync();
        try
        {
            if (_done) return;
            try
            {
                await _backup.CreateSafetyBackupAsync("sidecar-first-write");
                _logger.LogInformation("Sidecar first-write safety backup created.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sidecar first-write safety backup failed (continuing).");
            }
            _done = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}

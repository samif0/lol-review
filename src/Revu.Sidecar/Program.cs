#nullable enable

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Services;
using Revu.Sidecar;
using Velopack;

// ─────────────────────────────────────────────────────────────────────────────
// Revu localhost sidecar.
//
// Serves the dashboard JSON to the Tauri shell over loopback on an EPHEMERAL
// port (127.0.0.1:0 → OS picks a free port). A per-launch random bearer token
// gates every endpoint except /api/health. The actual bound port + token are
// written to %LOCALAPPDATA%\Revu\sidecar.json once Kestrel is listening so the
// Tauri host can read them and authenticate.
//
// SAFETY: the only DB access is via ReadOnlySqliteConnectionFactory, which opens
// SqliteOpenMode.ReadOnly. The sidecar never creates, writes, or migrates the
// database. This is a hard requirement for the migration phase.
// ─────────────────────────────────────────────────────────────────────────────

// MUST be the first thing the process does. Velopack's UpdateManager refuses to
// work until a VelopackLocator is initialised: `new UpdateManager(...)` throws
// "No VelopackLocator has been set. Either call VelopackApp.Build().Run() or
// provide IVelopackLocator..." — even inside a real install. Without this call
// UpdateService.Mgr() swallowed that throw, reported IsInstalled=false /
// CurrentVersion="dev", and NEVER queried the GitHub feed, so "Check for updates"
// silently found nothing. Run() bootstraps the locator from this exe's install
// layout (current/ + ../Update.exe), making the update check work.
//
// The Velopack *lifecycle hooks* (--veloapp-install/-updated/-obsolete/-uninstall,
// -firstrun) are owned by the Rust host (revu-desktop.exe is the registered
// mainExe; see desktop/src-tauri/src/main.rs). The sidecar is a child process and
// never receives those args, so Run() here only initialises the locator and
// returns immediately — it does not hijack the lifecycle.
VelopackApp.Build().Run();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Ephemeral loopback port; Kestrel reports the real one via the addresses feature.
builder.WebHost.UseUrls("http://127.0.0.1:0");

// Keep Kestrel HTTP/1.1 + small limits — this is a private loopback API.
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024; // we serve GETs; cap anything inbound
});

// ── Per-launch bearer token (256-bit, hex) ───────────────────────────────────
var bearerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

// ── DI: Core data infra (READ-ONLY), repositories, and the Core services the
//        dashboard needs. We register ONLY the Core graph — no App/WinUI/LCU/
//        hosted services. The pattern mirrors
//        Revu.App.Composition.ServiceCollectionExtensions but pared to the
//        read-only dashboard slice.
var services = builder.Services;

// Data infra — our read-only factory stands in for SqliteConnectionFactory.
// CRITICAL: never register the writable SqliteConnectionFactory here.
services.AddSingleton<IDbConnectionFactory, ReadOnlySqliteConnectionFactory>();

// Core services the dashboard (and its repos) depend on.
services.AddSingleton<IProtectedSecretStore, ProtectedSecretStore>();
services.AddSingleton<IConfigService, ConfigService>();
// BackupService is a GameRepository ctor dependency. It is only invoked from
// write paths (DeleteAsync) the read-only dashboard never calls, so wiring it
// is inert — no DB mutation happens during a dashboard read.
services.AddSingleton<IBackupService, BackupService>();

// Repositories. GameRepository implements the read-model query interfaces the
// dashboard uses (IGameHistoryQuery); register it once and expose the slice.
services.AddSingleton<GameRepository>();
services.AddSingleton<IGameHistoryQuery>(sp => sp.GetRequiredService<GameRepository>());
// IGameRepository.GetAsync(gameId) — used by ReviewSnapshotBuilder to load a
// SPECIFIC game when the review page is opened with ?gameId=N. Read-only call.
services.AddSingleton<IGameRepository>(sp => sp.GetRequiredService<GameRepository>());
services.AddSingleton<IObjectivesRepository, ObjectivesRepository>();
services.AddSingleton<IPromptsRepository, PromptsRepository>();
services.AddSingleton<IDeathClassificationsRepository, DeathClassificationsRepository>();
services.AddSingleton<IRulesRepository, RulesRepository>();
services.AddSingleton<IVodRepository, VodRepository>();
services.AddSingleton<ISessionLogRepository, SessionLogRepository>();
services.AddSingleton<IEvidenceRepository, EvidenceRepository>();
// VOD event timeline (kills/deaths/objectives → colored markers). Read-only.
// Also feeds the Review death audit (DEATH events → cause-chip rows).
services.AddSingleton<IGameEventsRepository, GameEventsRepository>();
// Review page matchup-note history (same champ vs enemy, past notes). Read-only.
services.AddSingleton<IMatchupNotesRepository, MatchupNotesRepository>();
// Tilt Check page read slice (recent history + stats + latest plan). Read-only;
// the reset RITUAL is a write that goes through WriteServices.TiltChecks instead.
services.AddSingleton<ITiltCheckRepository, TiltCheckRepository>();

// Objectives page needs the analytics read-model (GetRecentSpottedProblemsAsync).
// GameRepository implements it too — expose the slice off the same singleton.
services.AddSingleton<IGameAnalyticsQuery>(sp => sp.GetRequiredService<GameRepository>());

// Analytics page (read-only). IAnalysisService.GenerateProfileAsync builds the
// player-profile aggregates; its ctor needs IConceptTagRepository in addition to
// the analytics/history/sessionLog/objectives repos already registered above.
services.AddSingleton<IConceptTagRepository, ConceptTagRepository>();
services.AddSingleton<IAnalysisService, AnalysisService>();

// Snapshot builders (read-only equivalents of each page's ViewModel.LoadAsync).
services.AddSingleton<DashboardSnapshotBuilder>();
services.AddSingleton<VodSnapshotBuilder>();
services.AddSingleton<GamesSnapshotBuilder>();
services.AddSingleton<ObjectivesSnapshotBuilder>();
// Objective detail drill-downs (reached from an objective card). Both read-only;
// reuse the IObjectivesRepository / IEvidenceRepository / IVodRepository slices
// already registered above.
services.AddSingleton<ObjectiveGamesSnapshotBuilder>();
services.AddSingleton<ObjectiveNotesSnapshotBuilder>();
// Objective EDIT hydration (GET /api/objective?id=N): the full editable state of
// one objective (core fields + prompts + champions + criteria + focus phase) for
// the frontend Edit form. Reuses the read-graph IObjectivesRepository +
// IPromptsRepository already registered above.
services.AddSingleton<ObjectiveEditSnapshotBuilder>();
services.AddSingleton<ReviewSnapshotBuilder>();
services.AddSingleton<RulesSnapshotBuilder>();
services.AddSingleton<TiltCheckSnapshotBuilder>();
services.AddSingleton<PatternsSnapshotBuilder>();
// Config read snapshot (Settings page + cross-page config reads). Reuses the
// read-graph IConfigService registered above; BuildAsync forces a disk re-read
// so it reflects writes made via WriteServices.Config.
services.AddSingleton<ConfigSnapshotBuilder>();
// Settings DIAGNOSTIC reads (GET /api/settings/status): ffmpeg availability +
// Ascent folder status + clip-folder usage + the backups list. All read-only
// and DB-free (filesystem probes; backups dir enumeration). IClipService probes
// the filesystem for ffmpeg (no DB); IBackupService is already registered above.
services.AddSingleton<IClipService, ClipService>();
services.AddSingleton<SettingsStatusSnapshotBuilder>();
// Update check + download (Velopack UpdateManager). Apply/restart is the Rust
// host's job (it must run as the main exe); this only checks + stages.
services.AddSingleton<UpdateService>();
// Markdown export (GET /api/settings/export): IReviewExportService.ExportAllAsync
// is a pure read over the history/objectives/tags/prompts/vod/matchup/evidence
// repos already registered above. The sidecar returns the markdown string; the
// Tauri host writes it to disk via the save-file dialog (frontend-side).
services.AddSingleton<IReviewExportService, ReviewExportService>();

// WRITE graph — physically separate container bound to the WRITE-capable factory
// (reads above stay ReadOnly). Reuses Revu.Core write methods verbatim; the
// sidecar NEVER runs migrations. See WriteServices.cs.
services.AddSingleton<WriteServices>();

// ─────────────────────────────────────────────────────────────────────────────
// LCU LIVE SUBSYSTEM (Batch 5). The live champ-select / in-game capture pipeline.
//
// GameMonitorService polls the running League client and fires IMessenger
// messages (ChampSelect{Started,Updated,Cancelled}, GameStarted, GameInProgress,
// GameEnded, MissedReviews, LcuConnectionChanged) — the same bus the WinUI shell
// consumed. The SidecarGameFlowCoordinator (a hosted service) subscribes to those
// and (a) fans them out to the webview over SSE (GET /api/events) and (b) runs the
// END-OF-GAME game-save against the WriteServices graph so live-captured games
// keep recording. PreGameSnapshotBuilder serves the static intel deck.
//
// READ/WRITE SAFETY: the monitor + its capture/reconciliation services only READ
// (GameEndCaptureService pulls EOG stats over the LCU HTTP API; the reconciliation
// service queries match history). They are therefore wired to the read-only
// repositories registered above — no DB write can leak through the monitor. The
// single write hop (persisting the captured game) is isolated in the coordinator,
// which resolves IGameLifecycleWorkflowService from the WRITE container. This keeps
// the sidecar's hard read/write separation intact while still recording games.
//
// LCU SSL bypass: the LCU (https://127.0.0.1:{port}) + Live Client API
// (https://127.0.0.1:2999) present Riot's self-signed cert that can't chain to a
// trusted root. Accept it ONLY for loopback (mirror AppHostFactory.BypassSslValidation)
// so a future non-loopback reuse of these named clients fails closed.
static bool BypassLcuSsl(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
    => errors == SslPolicyErrors.None || request.RequestUri?.IsLoopback == true;

// Shared message bus (the same WeakReferenceMessenger the WinUI app uses).
services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

// Shared live-state + SSE fan-out hub the coordinator + read endpoints read.
services.AddSingleton<LcuLiveState>();
services.AddSingleton<SidecarEventHub>();

// LCU services (replicate AddLcuServices). The capture + reconciliation services
// take read-only repos already registered above (GameRepository / IGameRepository).
// IMissedGameDecisionRepository is read here only for the FindMissedGames query
// (the write path — MarkDismissed — goes through the WRITE graph's own copy).
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

services.AddSingleton<GameMonitorService>();
services.AddSingleton<IGameMonitorService>(sp => sp.GetRequiredService<GameMonitorService>());
services.AddHostedService(sp => sp.GetRequiredService<GameMonitorService>());

// The shell-equivalent message consumer: LCU → SSE + the EOG write. Hosted so it
// registers for messages on startup and unregisters on shutdown.
services.AddSingleton<SidecarGameFlowCoordinator>();
services.AddHostedService(sp => sp.GetRequiredService<SidecarGameFlowCoordinator>());

// Pre-game intel deck (GET /api/pregame). PreGameIntelService aggregates the
// rotating cards; it needs the read-graph repos above + IRiotChampionDataClient
// (CommunityDragon static champion data over plain HTTPS — no auth, disk-cached).
services.AddHttpClient<IRiotChampionDataClient, RiotChampionDataClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddSingleton<PreGameIntelService>();
services.AddSingleton<PreGameSnapshotBuilder>();

// JSON: camelCase wire shape, matches desktop/sample-dashboard.json.
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

var app = builder.Build();

// ── Additive schema upgrade (CRITICAL) ───────────────────────────────────────
// The WinUI app used to own DB migration; it's gone, so the sidecar — the only
// writer now — must apply new versioned migrations on startup or write endpoints
// hit "no such table" the moment a migration adds one (e.g. v8 objective_event_types).
// We run ONLY the additive, non-destructive subset (CREATE IF NOT EXISTS + ALTER
// migrations), via the WRITE factory, NEVER the normalize-rebuild/seed phases. A
// missing DB is a no-op (nothing to migrate yet); any failure is logged but does
// not block the sidecar from starting (read endpoints still work).
try
{
    var migrateLogger = app.Services.GetRequiredService<ILoggerFactory>();
    var writeFactory = new WriteSqliteConnectionFactory(
        migrateLogger.CreateLogger<WriteSqliteConnectionFactory>());
    if (File.Exists(writeFactory.DatabasePath))
    {
        var migrator = new DatabaseInitializer(
            writeFactory, migrateLogger.CreateLogger<DatabaseInitializer>());
        await migrator.ApplyAdditiveSchemaAsync();
    }
    else
    {
        migrateLogger.CreateLogger("Startup").LogInformation(
            "Schema upgrade skipped: no DB at {Path} yet.", writeFactory.DatabasePath);
    }
}
catch (Exception ex)
{
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup")
        .LogError(ex, "Additive schema upgrade failed at startup");
}

// ── Bearer-token middleware (constant-time compare; /api/health exempt) ──────
var expectedTokenBytes = Encoding.UTF8.GetBytes(bearerToken);
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api/health"))
    {
        await next();
        return;
    }

    if (!TryGetBearer(context.Request.Headers.Authorization.ToString(), out var presented)
        || !FixedTimeTokenEquals(expectedTokenBytes, presented))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }

    await next();
});

// ── GET /api/health (anonymous): opens a read-only conn + SELECT 1 ───────────
app.MapGet("/api/health", (IDbConnectionFactory factory, ILogger<Program> logger) =>
{
    try
    {
        using var conn = factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1;";
        cmd.ExecuteScalar();
        return Results.Json(new { status = "ready" }, jsonOptions);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Health probe: read-only DB open/select failed (degraded)");
        return Results.Json(new { status = "degraded" }, jsonOptions);
    }
});

// ── GET /api/dashboard (token-gated): the read-only snapshot ──────────────────
app.MapGet("/api/dashboard", async (DashboardSnapshotBuilder builder, CancellationToken ct) =>
{
    var snapshot = await builder.BuildAsync(ct);
    return Results.Json(snapshot, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// LCU LIVE CHANNEL (Batch 5).
// ─────────────────────────────────────────────────────────────────────────────

// ── GET /api/events (token-gated): Server-Sent Events stream of LCU messages ──
// The live wire to the webview. The SidecarGameFlowCoordinator publishes each LCU
// message (champ-select start/update/cancel, game start/in-progress/end, missed
// reviews, LCU connection) as a {type, payload} event onto SidecarEventHub; this
// endpoint streams them to a single connected client as text/event-stream. Each
// SSE record is `event: <type>\ndata: <json>\n\n`. The Tauri host opens this with
// the bearer token (browser EventSource can't set headers) and re-emits events to
// the webview, so the bearer stays server-side.
//
// On connect we replay the CURRENT live state as a synthetic `liveState` event so
// a webview that loads mid-flow (e.g. reopened during champ select) immediately
// knows the champ/enemy/role/in-progress without waiting for the next LCU tick.
app.MapGet("/api/events", async (HttpContext ctx, SidecarEventHub hub, LcuLiveState live, CancellationToken ct) =>
{
    var response = ctx.Response;
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    response.Headers.Connection = "keep-alive";
    // Disable response buffering so events flush immediately.
    response.Headers["X-Accel-Buffering"] = "no";

    var (reader, subscription) = hub.Subscribe();
    try
    {
        async Task WriteEventAsync(string type, object payload)
        {
            var json = JsonSerializer.Serialize(payload, jsonOptions);
            await response.WriteAsync($"event: {type}\n", ct);
            await response.WriteAsync($"data: {json}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }

        // 1) Initial connection event + current live-state replay.
        await WriteEventAsync("connected", new { ok = true });
        await WriteEventAsync("liveState", new
        {
            myChampion = live.MyChampion,
            enemyChampion = live.EnemyChampion,
            myPosition = live.MyPosition,
            participantMapJson = live.ParticipantMapJson,
            sessionKey = live.SessionKey,
            isGameInProgress = live.IsGameInProgress,
            lcuConnected = live.IsLcuConnected,
        });

        // 2) Stream events until the client disconnects. A periodic comment frame
        //    (": keep-alive") keeps the connection from idling out when the LCU is
        //    quiet (no game running).
        while (!ct.IsCancellationRequested)
        {
            SidecarEventHub.SidecarEvent evt;
            try
            {
                using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, heartbeat.Token);
                evt = await reader.ReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Heartbeat tick — send a comment frame and keep waiting.
                await response.WriteAsync(": keep-alive\n\n", ct);
                await response.Body.FlushAsync(ct);
                continue;
            }
            await WriteEventAsync(evt.Type, evt.Payload);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — normal SSE teardown.
    }
    finally
    {
        subscription.Dispose();
    }
});

// ── GET /api/pregame[?myChampion=&enemy=&role=&participantMap=] (token-gated) ──
// The STATIC champ-select / in-game intel deck: rotating intel cards, active +
// priority objectives + their pre-game custom prompts (draft answers prefilled),
// saved matchup notes, the intent carry-over seeds (carry / objective / adherence)
// with provenance, the latest if-then plan, and the mood/intention gates. Mirrors
// the READ half of PreGameDialogViewModel.LoadAsync. The LIVE champ-select data
// (my champ / enemy / role / map → live matchup + 2v2 pairing) arrives over the
// SSE channel; the query params (or the live state) seed the matchup card at load.
app.MapGet("/api/pregame", async (
    string? myChampion, string? enemy, string? role, string? participantMap,
    PreGameSnapshotBuilder builder, CancellationToken ct) =>
{
    var snapshot = await builder.BuildAsync(myChampion, enemy, role, participantMap, ct);
    return Results.Json(snapshot, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// DEFERRED PRE-GAME SNAPSHOT WRITES (Batch 5). These are NOT DB writes — they
// stage the user's champ-select choices (mood / intent / practiced objective ids)
// into the in-memory LcuLiveState, which the SidecarGameFlowCoordinator reads and
// persists to session_log at game END (exactly like the WinUI PreGameDialogViewModel
// statics → ShellViewModel hop). The one champ-select write that DOES hit the DB —
// the per-prompt draft answer — reuses the existing POST /api/prompt/answer/save's
// sibling draft path below. No backup guard needed (no DB mutation here).
// ─────────────────────────────────────────────────────────────────────────────

// POST /api/pregame/mood  { mood }  — 1..5 (Tilted/Off/Neutral/Good/LockedIn).
app.MapPost("/api/pregame/mood", (PreGameMoodBody body, LcuLiveState live) =>
{
    live.SetMood(body?.Mood ?? 0);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/pregame/intent  { intention?, source?, cleared? } — the THIS GAME'S
// INTENT card state. source ∈ carry|objective|edited (adherence maps to objective).
app.MapPost("/api/pregame/intent", (PreGameIntentBody body, LcuLiveState live) =>
{
    live.SetIntent(body?.Intention ?? "", body?.Source ?? "", body?.Cleared ?? false);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/pregame/practiced  { objectiveIds:[...] } — the practiced-toggle set.
app.MapPost("/api/pregame/practiced", (PreGamePracticedBody body, LcuLiveState live) =>
{
    live.SetPracticed(body?.ObjectiveIds ?? new List<long>());
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/pregame/prompt/draft  { promptId, text } — autosave a champ-select
// prompt answer to pre_game_draft_prompts under the current live session key, so
// it survives a webview reload and is promoted to the game row at EOG. This is the
// ONE champ-select interaction that writes to the DB (a single upsert). Mirrors
// PreGamePromptAnswer.AnswerText → SaveDraftAnswerAsync. Backup-guarded like the
// other writes; no-op (ok:false) when no live champ-select session is active.
app.MapPost("/api/pregame/prompt/draft", async (PreGameDraftBody body, WriteServices w, LcuLiveState live, ILogger<Program> log) =>
{
    if (body is null || body.PromptId <= 0)
        return Results.BadRequest(new { error = "promptId required" });
    var sessionKey = live.SessionKey;
    if (string.IsNullOrEmpty(sessionKey))
        return Results.Json(new { ok = false, error = "no active champ-select session" }, jsonOptions);
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Prompts.SaveDraftAnswerAsync(sessionKey, body.PromptId, body.Text ?? "");
    log.LogInformation("Pre-game prompt draft saved: prompt {PromptId} (session {Session})", body.PromptId, sessionKey);
    return Results.Json(new { ok = true }, jsonOptions);
});

// ── GET /api/games[?view=queue|today|history|vod][&page=N] (token-gated) ─────
// Read-only games-workspace snapshot. view selects one of the four list views
// (Queue=unreviewed 14d / Today / History / VOD-on-disk); unknown/missing →
// queue. page is the zero-based History page (offset page*30); ignored by the
// single-shot views. hasMore is true only on History when more pages remain.
app.MapGet("/api/games", async (string? view, int? page, GamesSnapshotBuilder builder, CancellationToken ct) =>
{
    var snapshot = await builder.BuildAsync(view, page ?? 0, ct);
    return Results.Json(snapshot, jsonOptions);
});

// ── GET /api/objectives (token-gated): read-only objectives snapshot ─────────
app.MapGet("/api/objectives", async (ObjectivesSnapshotBuilder builder, CancellationToken ct) =>
{
    var snapshot = await builder.BuildAsync(ct);
    return Results.Json(snapshot, jsonOptions);
});

// ── GET /api/objective/games?id=N (token-gated): one objective's linked games +
// its evidence ledger. Read-only drill-down reached from an objective card; the
// per-row Watch VOD / Review jumps are plain frontend navigation.
app.MapGet("/api/objective/games", async (long id, ObjectiveGamesSnapshotBuilder builder, CancellationToken ct) =>
    Results.Json(await builder.BuildAsync(id, ct), jsonOptions));

// ── GET /api/objective/notes?id=N (token-gated): one objective's aggregated
// review notes + execution notes + clips/bookmarks. Read-only aggregator; each
// row jumps back to review / vodplayer via plain frontend navigation.
app.MapGet("/api/objective/notes", async (long id, ObjectiveNotesSnapshotBuilder builder, CancellationToken ct) =>
    Results.Json(await builder.BuildAsync(id, ct), jsonOptions));

// ── GET /api/objective?id=N (token-gated): FULL edit hydration for one objective.
// Mirrors ObjectivesViewModel.BeginEditObjectiveAsync — core fields + multi-phase
// flags + structured criterion + focus phase + custom prompts + champion gate +
// the played-champion typeahead list. Read-only; the actual save goes through the
// POST /api/objective/create|update write endpoints. 404 when the id doesn't
// resolve. The criteria-metric picker options ride along so the form can build its
// dropdown without hardcoding the metric list.
app.MapGet("/api/objective", async (long id, ObjectiveEditSnapshotBuilder builder, CancellationToken ct) =>
{
    var dto = await builder.BuildAsync(id, ct);
    if (dto is null) return Results.NotFound(new { error = "objective not found" });
    return Results.Json(
        new { objective = dto, criteriaMetrics = ObjectiveEditSnapshotBuilder.BuildCriteriaMetricOptions() },
        jsonOptions);
});

// ── GET /api/vod?gameId=N (token-gated): VOD file path + bookmarks ───────────
app.MapGet("/api/vod", async (long gameId, VodSnapshotBuilder builder, CancellationToken ct) =>
    Results.Json(await builder.BuildAsync(gameId, ct), jsonOptions));

// ── GET /api/review[?gameId=N] (token-gated): single-game review snapshot ────
// With gameId, loads THAT game (clicking a game row); without, the sample subject.
app.MapGet("/api/review", async (long? gameId, ReviewSnapshotBuilder builder, CancellationToken ct) =>
{
    var snapshot = await builder.BuildAsync(gameId, ct);
    return Results.Json(snapshot, jsonOptions);
});

// ── GET /api/rules (token-gated): list of the user's rules ───────────────────
// Lists active + inactive rules with their live RULE CHECK state + behavioral
// evidence. The full CRUD (create / update / toggle / delete) lives in the four
// POST endpoints below; the snapshot is the read half.
app.MapGet("/api/rules", async (RulesSnapshotBuilder b, CancellationToken ct) =>
    Results.Json(await b.BuildAsync(ct), jsonOptions));

// ─────────────────────────────────────────────────────────────────────────────
// Rules CRUD writes (token-gated). Reuse RulesRepository (the schema-tolerant
// repo the WinUI app writes through) verbatim — no hand-rolled SQL. Each takes
// the one-time session safety backup before writing, then the frontend refetches
// GET /api/rules. loss_streak encodes its optional cooldown as "threshold:minutes"
// in conditionValue, exactly like RulesViewModel.CreateRuleAsync builds it (the
// frontend assembles that string before posting).
// ─────────────────────────────────────────────────────────────────────────────

// POST /api/rule/create  { name, ruleType?, conditionValue?, description?, replacementPlan? }
app.MapPost("/api/rule/create", async (CreateRuleBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "name required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    var id = await w.Rules.CreateAsync(
        body.Name.Trim(),
        body.Description?.Trim() ?? "",
        string.IsNullOrWhiteSpace(body.RuleType) ? "custom" : body.RuleType,
        body.ConditionValue?.Trim() ?? "",
        body.ReplacementPlan?.Trim() ?? "");
    log.LogInformation("Rule created: {Id} '{Name}' ({Type})", id, body.Name, body.RuleType);
    return Results.Json(new { ok = true, id }, jsonOptions);
});

// POST /api/rule/update  { id, name, ruleType?, conditionValue?, description?, replacementPlan? }
app.MapPost("/api/rule/update", async (UpdateRuleBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.Id <= 0 || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "id and name required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Rules.UpdateAsync(
        body.Id,
        body.Name.Trim(),
        body.Description?.Trim() ?? "",
        string.IsNullOrWhiteSpace(body.RuleType) ? "custom" : body.RuleType,
        body.ConditionValue?.Trim() ?? "",
        body.ReplacementPlan?.Trim() ?? "");
    log.LogInformation("Rule updated: {Id}", body.Id);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/rule/toggle  { id } — flip active/inactive (RulesRepository.ToggleAsync).
app.MapPost("/api/rule/toggle", async (RuleIdBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Rules.ToggleAsync(body.Id);
    log.LogInformation("Rule toggled: {Id}", body.Id);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/rule/delete  { id } — HARD delete (DELETE FROM rules). The frontend
// confirms before calling; the first-write safety backup is the net here.
app.MapPost("/api/rule/delete", async (RuleIdBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Rules.DeleteAsync(body.Id);
    log.LogInformation("Rule deleted: {Id}", body.Id);
    return Results.Json(new { ok = true }, jsonOptions);
});

// ── GET /api/tiltcheck (token-gated): read-only tilt-reset history + stats ────
// Recent rituals (≤20), aggregate before/after stats, and the latest if-then
// plan (≤14d). The reset RITUAL is a WRITE the frontend runs via
// invoke('run_reset', …) → POST /api/reset; this snapshot is READ-ONLY.
app.MapGet("/api/tiltcheck", async (TiltCheckSnapshotBuilder b, CancellationToken ct) =>
    Results.Json(await b.BuildAsync(ct), jsonOptions));

// ── GET /api/patterns (token-gated): read-only cross-game pattern cards ───────
// Pattern cards + their ordered moment playlists. Mark-reviewed (and the
// per-moment note/clip writes) are WRITE ops and are DEFERRED — the DTO carries
// isReviewed + a carryForwardNote placeholder the frontend renders read-only.
app.MapGet("/api/patterns", async (PatternsSnapshotBuilder b, CancellationToken ct) =>
    Results.Json(await b.BuildAsync(ct), jsonOptions));

// ── GET /api/config (token-gated): read-only app-config snapshot ─────────────
// Editable fields the Settings page round-trips + the derived/cross-page reads
// (IsAscentEnabled, AscentReminderDismissed, AutoTimelineClippingHintDismissed).
// Forces a disk re-read so it reflects POST /api/config/save. Secrets excluded.
app.MapGet("/api/config", async (ConfigSnapshotBuilder b, CancellationToken ct) =>
    Results.Json(await b.BuildAsync(ct), jsonOptions));

// ── GET /api/settings/status (token-gated): read-only Settings diagnostics ───
// ffmpeg availability + Ascent folder status + clip-folder usage + the backups
// list. All filesystem/DB-free reads (backups dir enumeration is not a DB read).
// Pairs with GET /api/config (the editable surface) to fully hydrate the page.
app.MapGet("/api/settings/status", async (SettingsStatusSnapshotBuilder b, CancellationToken ct) =>
    Results.Json(await b.BuildAsync(ct), jsonOptions));

// GET /api/update/check — ask the GitHub release feed whether a newer version
// exists (Velopack UpdateManager). Read-only; never throws. The host polls this on
// launch (banner) and Settings (manual check). Returns the UpdateCheckResult shape.
app.MapGet("/api/update/check", async (UpdateService upd) =>
    Results.Json(await upd.CheckAsync(), jsonOptions));

// POST /api/update/download — stage the discovered update's package locally so the
// Rust host can apply it via Update.exe. Returns { ok, packagePath, version }.
app.MapPost("/api/update/download", async (UpdateService upd) =>
    Results.Json(await upd.DownloadAsync(), jsonOptions));

// ── GET /api/settings/export (token-gated): build the Markdown review export ──
// Pure READ: IReviewExportService.ExportAllAsync bundles games/notes/prompts/
// objectives/tags/matchup notes/VOD links/bookmarks into a single Markdown
// string. The sidecar does NOT write the file — it returns { markdown, fileName }
// and the Tauri host saves it via the native save-file dialog + fs plugin. The
// suggested filename mirrors the WinUI picker (revu-review-export-{yyyyMMdd-HHmm}).
app.MapGet("/api/settings/export", async (IReviewExportService export, ILogger<Program> log, CancellationToken ct) =>
{
    var markdown = await export.ExportAllAsync(ct);
    var fileName = $"revu-review-export-{DateTime.Now:yyyyMMdd-HHmm}.md";
    log.LogInformation("Review export built ({Chars} chars) -> {FileName}", markdown.Length, fileName);
    return Results.Json(new { ok = true, markdown, fileName }, jsonOptions);
});

// POST /api/settings/reset — DESTRUCTIVE: wipe all data and start fresh. The Core
// IBackupService.ResetAllDataAsync ALWAYS takes a full backup FIRST (returns its
// path), then clears, so this can never blind-overwrite. The Tauri host relaunches
// the app on success. Returns { ok, backupPath } or { ok:false, error }.
app.MapPost("/api/settings/reset", async (WriteServices w, ILogger<Program> log) =>
{
    var result = await w.Backup.ResetAllDataAsync();
    if (!result.Success)
        return Results.Json(new { ok = false, error = result.ErrorMessage ?? "Reset failed." }, jsonOptions, statusCode: 422);
    log.LogWarning("Reset all data (backup at {BackupPath})", result.BackupFilePath);
    return Results.Json(new { ok = true, backupPath = result.BackupFilePath }, jsonOptions);
});

// POST /api/settings/restore { backupFilePath } — DESTRUCTIVE: replace the live DB
// with a chosen backup. Core RestoreFromBackupAsync takes a PRE-RESTORE safety
// backup FIRST (returns its path), then swaps in the chosen file. The Tauri host
// relaunches on success. Returns { ok, preRestoreBackupPath } or { ok:false, error }.
app.MapPost("/api/settings/restore", async (RestoreBackupBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.BackupFilePath))
        return Results.BadRequest(new { error = "backupFilePath required" });
    var result = await w.Backup.RestoreFromBackupAsync(body.BackupFilePath);
    if (!result.Success)
        return Results.Json(new { ok = false, error = result.ErrorMessage ?? "Restore failed." }, jsonOptions, statusCode: 422);
    log.LogWarning("Restored backup {Path} (pre-restore backup at {PreBackup})", body.BackupFilePath, result.PreRestoreBackupFilePath);
    return Results.Json(new { ok = true, preRestoreBackupPath = result.PreRestoreBackupFilePath }, jsonOptions);
});

// GET /api/review/export?gameId=N — SINGLE-game review markdown (for the review
// page's Copy + Export). Reuses ReviewExportService.ExportGameAsync (returns null
// when the game doesn't exist). The review page copies the markdown to the
// clipboard or saves it via the native dialog (save_export_file).
app.MapGet("/api/review/export", async (long gameId, IReviewExportService export, ILogger<Program> log, CancellationToken ct) =>
{
    if (gameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    var markdown = await export.ExportGameAsync(gameId, ct);
    if (markdown is null)
        return Results.Json(new { ok = true, found = false }, jsonOptions);
    var fileName = $"revu-{gameId}-review.md";
    log.LogInformation("Single-game review export built ({Chars} chars) -> {FileName}", markdown.Length, fileName);
    return Results.Json(new { ok = true, found = true, markdown, fileName }, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// WRITE endpoints (token-gated). Non-destructive daily-loop actions only.
// Each takes a one-time safety backup before the first write of the session.
// DELETE-GAME is deliberately NOT here (deferred destructive op).
// ─────────────────────────────────────────────────────────────────────────────
var today = () => DateTime.Now.ToString("yyyy-MM-dd");

// Pattern-moment auto-clip padding for single-point moments (mirror
// PatternReviewViewModel.ClipLeadSeconds / ClipTrailSeconds): a point moment
// (start==end, e.g. a death) clips a watchable window around it.
const int PatternClipLeadSeconds = 8;
const int PatternClipTrailSeconds = 4;

// POST /api/block/start  { intention }
app.MapPost("/api/block/start", async (StartBlockBody body, WriteServices w, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(body?.Intention))
        return Results.BadRequest(new { error = "intention required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.SessionLog.SetSessionIntentionAsync(today(), body.Intention.Trim());
    log.LogInformation("Start block: intention set for {Date}", today());
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/block/end  { rating, note?, date? }
// date targets the open block's own row (it can be a prior day when a block carried
// over unfinished). Only a well-formed yyyy-MM-dd is honored; anything else falls
// back to today so a malformed value can't write to an arbitrary row.
app.MapPost("/api/block/end", async (EndBlockBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.Rating < 1 || body.Rating > 10)
        return Results.BadRequest(new { error = "rating must be 1-10" });
    var targetDate = today();
    if (!string.IsNullOrWhiteSpace(body.Date)
        && DateTime.TryParseExact(body.Date, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _))
    {
        targetDate = body.Date;
    }
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.SessionLog.SaveSessionDebriefAsync(targetDate, body.Rating, body.Note ?? "");
    log.LogInformation("End block: debrief saved ({Rating}/10) for {Date}", body.Rating, targetDate);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/review/save — the full post-game review write (multi-table, via the
// SAME IReviewWorkflowService.SaveAsync the WinUI app uses).
app.MapPost("/api/review/save", async (SaveReviewBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();

    var snapshot = new Revu.Core.Services.ReviewSnapshot(
        MentalRating: body.MentalRating,
        WentWell: body.WentWell ?? "",
        Mistakes: body.Mistakes ?? "",
        FocusNext: body.FocusNext ?? "",
        ReviewNotes: body.ReviewNotes ?? "",
        ImprovementNote: body.ImprovementNote ?? "",
        Attribution: body.Attribution ?? "",
        MentalHandled: body.MentalHandled ?? "",
        SpottedProblems: body.SpottedProblems ?? "",
        OutsideControl: body.OutsideControl ?? "",
        WithinControl: body.WithinControl ?? "",
        PersonalContribution: body.PersonalContribution ?? "",
        EnemyLaner: body.EnemyLaner ?? "",
        MatchupNote: body.MatchupNote ?? "",
        SelectedTagIds: body.SelectedTagIds ?? new List<long>(),
        ObjectivePractices: (body.ObjectivePractices ?? new List<ObjectivePracticeBody>())
            .Select(p => new Revu.Core.Services.SaveObjectivePracticeRequest(p.ObjectiveId, p.Practiced, p.ExecutionNote ?? ""))
            .ToList(),
        FocusAdherence: body.FocusAdherence);

    var request = new Revu.Core.Services.SaveReviewRequest(
        GameId: body.GameId,
        ChampionName: body.ChampionName ?? "",
        Win: body.Win,
        RequireReviewNotes: false,
        Snapshot: snapshot);

    var result = await w.ReviewWorkflow.SaveAsync(request, ct);
    if (!result.Success)
        return Results.Json(new { ok = false, error = result.ErrorMessage }, jsonOptions, statusCode: 422);
    log.LogInformation("Review saved for game {GameId}", body.GameId);
    return Results.Json(new { ok = true, savedEnemyLaner = result.SavedEnemyLaner }, jsonOptions);
});

// POST /api/review/skip  { gameId } — mark reviewed without opening.
app.MapPost("/api/review/skip", async (GameIdBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.SessionLog.MarkSkippedAsync(body.GameId);
    log.LogInformation("Review skipped for game {GameId}", body.GameId);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/review/delete  { gameId } — DELETE a saved review, returning the game
// to the unreviewed queue. Clears the review text + session_log review markers +
// concept tags + matchup note + draft, but PRESERVES objective progress and the
// session_log behavioral fields (mental/adherence streaks stay intact). Keeps the
// game row (this is NOT a game delete). Backup guard runs first, like every write.
app.MapPost("/api/review/delete", async (GameIdBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    var result = await w.ReviewWorkflow.DeleteAsync(body.GameId, ct);
    if (!result.Success)
        return Results.Json(new { ok = false, error = result.ErrorMessage }, jsonOptions, statusCode: 422);
    log.LogInformation("Review deleted for game {GameId}", body.GameId);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/objective/create — create an objective AND persist its full editing
// surface (prompts / champions / focus-phase / structured criterion). Mirrors
// ObjectivesViewModel.CreateObjectiveAsync: at least one practice phase must be
// checked; minis clamp target to max(1,N); the side-tables (prompts diff-save,
// champion gate replace, focus phase, criterion) are written after the core row.
app.MapPost("/api/objective/create", async (CreateObjectiveBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.Title))
        return Results.BadRequest(new { error = "title required" });
    // Mirror CanCreate: at least one of pre/in/post must be checked.
    if (!body.PracticePre && !body.PracticeIn && !body.PracticePost)
        return Results.BadRequest(new { error = "at least one practice phase required" });

    await w.BackupGuard.EnsureBackedUpAsync();

    var type = NormalizeObjectiveType(body.Type);
    var targetGameCount = type == "mini" ? Math.Max(1, body.TargetGameCount) : 0;

    var id = await w.Objectives.CreateWithPhasesAndTargetAsync(
        body.Title.Trim(), (body.SkillArea ?? "").Trim(), type,
        (body.CompletionCriteria ?? "").Trim(), (body.Description ?? "").Trim(),
        body.PracticePre, body.PracticeIn, body.PracticePost,
        targetGameCount);

    await PersistObjectiveSideTablesAsync(w, id, body.Prompts, body.Champions,
        body.FocusPhaseIndex, body.CriteriaMetricIndex, body.CriteriaOpIndex, body.CriteriaValueText,
        body.EventTypes);

    log.LogInformation("Objective created: {Id} '{Title}'", id, body.Title);
    return Results.Json(new { ok = true, id }, jsonOptions);
});

// POST /api/objective/update — update an objective AND its full editing surface.
// Mirrors the EditingObjectiveId branch of CreateObjectiveAsync: UpdateWithPhases
// + UpdateTargetGameCount, then the same side-table persist as create.
app.MapPost("/api/objective/update", async (UpdateObjectiveBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.Id <= 0 || string.IsNullOrWhiteSpace(body.Title))
        return Results.BadRequest(new { error = "id and title required" });
    if (!body.PracticePre && !body.PracticeIn && !body.PracticePost)
        return Results.BadRequest(new { error = "at least one practice phase required" });

    await w.BackupGuard.EnsureBackedUpAsync();

    var type = NormalizeObjectiveType(body.Type);
    var targetGameCount = type == "mini" ? Math.Max(1, body.TargetGameCount) : 0;

    await w.Objectives.UpdateWithPhasesAsync(
        body.Id, body.Title.Trim(), (body.SkillArea ?? "").Trim(), type,
        (body.CompletionCriteria ?? "").Trim(), (body.Description ?? "").Trim(),
        body.PracticePre, body.PracticeIn, body.PracticePost);
    await w.Objectives.UpdateTargetGameCountAsync(body.Id, targetGameCount);

    await PersistObjectiveSideTablesAsync(w, body.Id, body.Prompts, body.Champions,
        body.FocusPhaseIndex, body.CriteriaMetricIndex, body.CriteriaOpIndex, body.CriteriaValueText,
        body.EventTypes);

    log.LogInformation("Objective updated: {Id}", body.Id);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/objective/delete  { id } — hard delete. The Core repo cascades the
// prompts/answers/game_objectives/champion rows. The frontend confirms first; the
// session safety backup is taken before the first write.
app.MapPost("/api/objective/delete", async (ObjectiveIdBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Objectives.DeleteAsync(body.Id);
    log.LogInformation("Objective deleted: {Id}", body.Id);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/objective/priority  { id }
app.MapPost("/api/objective/priority", async (ObjectiveIdBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Objectives.SetPriorityAsync(body.Id);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/objective/complete  { id }
app.MapPost("/api/objective/complete", async (ObjectiveIdBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Objectives.MarkCompleteAsync(body.Id);
    log.LogInformation("Objective completed: {Id}", body.Id);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/reset — save a tilt-reset ritual result.
app.MapPost("/api/reset", async (ResetBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.Emotion))
        return Results.BadRequest(new { error = "emotion required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.TiltChecks.SaveAsync(
        emotion: body.Emotion.Trim(),
        intensityBefore: body.IntensityBefore,
        intensityAfter: body.IntensityAfter,
        reframeThought: body.ReframeThought ?? "",
        reframeResponse: body.ReframeResponse ?? "",
        ifThenPlan: body.IfThenPlan ?? "");
    log.LogInformation("Tilt reset saved ({Emotion})", body.Emotion);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/config/save — read-modify-write the app config (Settings page +
// dismiss-flag writers). Mirrors SettingsViewModel.SaveCommand EXACTLY: load the
// whole config, mutate ONLY the fields present in the body (so unrelated keys —
// secrets, keybinds, puuid — are never clobbered), then SaveAsync. Every field
// is nullable in the body; a null means "leave unchanged".
//
// P-023 defense-in-depth for the FOLDER paths: an empty/whitespace folder string
// is treated as "leave unchanged" too — NOT as "blank the saved path". A save made
// before the Settings page finished rendering (or any caller that sends "") would
// otherwise zero ascent_folder/clips_folder/backup_folder over the real values,
// which is exactly how all three got blanked while riot_* survived. A DELIBERATE
// clear (the Clear button) sends the FolderClearSentinel, which maps to "".
app.MapPost("/api/config/save", async (SaveConfigBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null) return Results.BadRequest(new { error = "body required" });
    await w.BackupGuard.EnsureBackedUpAsync();

    var cfg = await w.Config.LoadAsync();

    // Folder fields: null/empty = unchanged; sentinel = explicit clear; else set.
    if (ConfigSaveGuards.TryResolveFolderWrite(body.AscentFolder, out var ascent)) cfg.AscentFolder = ascent;
    if (body.AscentReminderDismissed is not null) cfg.AscentReminderDismissed = body.AscentReminderDismissed.Value;
    if (ConfigSaveGuards.TryResolveFolderWrite(body.ClipsFolder, out var clips)) cfg.ClipsFolder = clips;
    if (body.ClipsMaxSizeMb is not null) cfg.ClipsMaxSizeMb = body.ClipsMaxSizeMb.Value;
    if (body.BackupEnabled is not null) cfg.BackupEnabled = body.BackupEnabled.Value;
    if (ConfigSaveGuards.TryResolveFolderWrite(body.BackupFolder, out var backup)) cfg.BackupFolder = backup;
    if (body.TiltFixMode is not null) cfg.TiltFixMode = body.TiltFixMode.Value;
    if (body.RequireReviewNotes is not null) cfg.RequireReviewNotes = body.RequireReviewNotes.Value;
    if (body.SidebarAnimationEnabled is not null) cfg.SidebarAnimationEnabled = body.SidebarAnimationEnabled.Value;
    if (body.MinimizeDuringGame is not null) cfg.MinimizeDuringGame = body.MinimizeDuringGame.Value;
    if (body.AutoTimelineClippingEnabled is not null) cfg.AutoTimelineClippingEnabled = body.AutoTimelineClippingEnabled.Value;
    if (body.AutoTimelineClippingHintDismissed is not null) cfg.AutoTimelineClippingHintDismissed = body.AutoTimelineClippingHintDismissed.Value;
    if (body.RiotId is not null) cfg.RiotId = body.RiotId.Trim();
    // Region is lower-cased on Save (mirror SettingsViewModel).
    if (body.Region is not null) cfg.RiotRegion = body.Region.Trim().ToLowerInvariant();
    if (body.PrimaryRole is not null) cfg.PrimaryRole = body.PrimaryRole;
    // Onboarding ROLE-FINISH writes land here: the wizard's final step saves
    // PrimaryRole on both paths, and on the SKIP path also stamps
    // OnboardingSkipped=true (mirror OnboardingViewModel.FinishRoleAsync). The
    // login path leaves this null — /api/auth/resolve already set it false.
    if (body.OnboardingSkipped is not null) cfg.OnboardingSkipped = body.OnboardingSkipped.Value;

    await w.Config.SaveAsync(cfg);
    log.LogInformation("Config saved via sidecar.");
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/settings/scan-vods — scan the Ascent folder for recordings and
// auto-match them to unlinked games. This is a read+WRITE (AutoMatchRecordings-
// Async persists the new match links), so it runs through the WRITE graph behind
// the session backup guard. Mirrors SettingsPage.OnScanVodsClick EXACTLY: count
// recordings, auto-match, then surface the P-007 unmatched-recent diagnostic
// (recordings from the last 7 days that match no linked game). Returns the result
// text the page renders; the unmatched-recent leg is best-effort (swallowed).
app.MapPost("/api/settings/scan-vods", async (WriteServices w, ILogger<Program> log) =>
{
    await w.BackupGuard.EnsureBackedUpAsync();
    try
    {
        var recordings = await w.VodScan.FindRecordingsAsync();
        var matched = await w.VodScan.AutoMatchRecordingsAsync();

        // P-007: surface silent misses — a recent recording matching no game is
        // exactly the failure the user can't otherwise see.
        var unmatchedNote = "";
        try
        {
            var linkedPaths = new HashSet<string>(
                (await w.Vod.GetAllVodsAsync()).Select(v => v.FilePath),
                StringComparer.OrdinalIgnoreCase);
            var weekAgo = DateTimeOffset.Now.AddDays(-7).ToUnixTimeSeconds();
            var unmatchedRecent = recordings.Count(r => r.Mtime >= weekAgo && !linkedPaths.Contains(r.Path));
            if (unmatchedRecent > 0)
                unmatchedNote = $" {unmatchedRecent} recording(s) from the last 7 days match no game.";
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Scan unmatched-recent diagnostic failed (swallowed)");
        }

        string text;
        if (matched > 0)
            text = $"Matched {matched} VOD(s) to games! ({recordings.Count} recordings found){unmatchedNote}";
        else if (recordings.Count == 0)
            text = "No video files found. Check that your Ascent folder is set and contains recordings.";
        else
            text = $"Found {recordings.Count} recordings but no new matches. Games may already be linked or outside the match window.{unmatchedNote}";

        log.LogInformation("VOD scan: matched {Matched}, recordings {Count}", matched, recordings.Count);
        return Results.Json(new { ok = true, matched, recordingCount = recordings.Count, text }, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "VOD scan failed");
        return Results.Json(new { ok = false, error = ex.Message, text = $"Scan failed: {ex.Message}" }, jsonOptions);
    }
});

// ─────────────────────────────────────────────────────────────────────────────
// DESTRUCTIVE write: delete a game. Unlike the daily-loop writes above this is
// irreversible inside the app — IGameRepository.DeleteAsync cascades across the
// child tables in a single transaction AND snapshots a DB backup BEFORE any
// mutation (returns the backup path). We ALSO take the session-first-write
// safety backup (belt-and-suspenders). The frontend confirms before calling.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/game/delete", async (GameIdBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    var backupPath = await w.Games.DeleteAsync(body.GameId);
    log.LogInformation("Game {GameId} deleted (backup at {BackupPath})", body.GameId, backupPath);
    return Results.Json(new { ok = true, backupPath }, jsonOptions);
});

// POST /api/review/draft/save — persist an in-progress review WITHOUT finalizing
// it, so navigating away (e.g. to the VOD player) doesn't lose edits. Mirrors
// ReviewViewModel.WatchVodCommand: SaveDraftAsync(ReviewDraftRequest(GameId,
// BuildSnapshot())). Reuses the SAME ReviewSnapshot shape POST /api/review/save
// builds — body fields are identical minus championName/win/requireReviewNotes.
app.MapPost("/api/review/draft/save", async (SaveReviewDraftBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();

    var snapshot = new Revu.Core.Services.ReviewSnapshot(
        MentalRating: body.MentalRating,
        WentWell: body.WentWell ?? "",
        Mistakes: body.Mistakes ?? "",
        FocusNext: body.FocusNext ?? "",
        ReviewNotes: body.ReviewNotes ?? "",
        ImprovementNote: body.ImprovementNote ?? "",
        Attribution: body.Attribution ?? "",
        MentalHandled: body.MentalHandled ?? "",
        SpottedProblems: body.SpottedProblems ?? "",
        OutsideControl: body.OutsideControl ?? "",
        WithinControl: body.WithinControl ?? "",
        PersonalContribution: body.PersonalContribution ?? "",
        EnemyLaner: body.EnemyLaner ?? "",
        MatchupNote: body.MatchupNote ?? "",
        SelectedTagIds: body.SelectedTagIds ?? new List<long>(),
        ObjectivePractices: (body.ObjectivePractices ?? new List<ObjectivePracticeBody>())
            .Select(p => new Revu.Core.Services.SaveObjectivePracticeRequest(p.ObjectiveId, p.Practiced, p.ExecutionNote ?? ""))
            .ToList(),
        FocusAdherence: body.FocusAdherence);

    var request = new Revu.Core.Services.ReviewDraftRequest(GameId: body.GameId, Snapshot: snapshot);

    var ok = await w.ReviewWorkflow.SaveDraftAsync(request, ct);
    log.LogInformation("Review draft saved for game {GameId} (ok={Ok})", body.GameId, ok);
    return Results.Json(new { ok }, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// REVIEW-PAGE granular writes (Batch 2). Unlike POST /api/review/save (the whole
// form at once), these are the SINGLE-FIELD, persist-immediately interactions:
// one-tap death cause chips, evidence triage, prompt answers, focus adherence.
// Each reuses a Revu.Core write method verbatim. All token-gated + backup-guarded.
// ─────────────────────────────────────────────────────────────────────────────

// ── SHARED EVIDENCE TRIAGE (Review + VOD pages both POST these) ───────────────
// The evidence "inbox" rows are written by both the Review page (EVIDENCE TO SORT
// + per-objective ATTACHED) and the VOD player. Three single-field upserts.

// POST /api/evidence/polarity  { evidenceId, polarity }  (good|neutral|bad)
// Mirrors ReviewViewModel.SetEvidencePolarityAsync: set the polarity, and if the
// row was still needs_review promote it to evidence (a triaged judgement).
app.MapPost("/api/evidence/polarity", async (EvidencePolarityBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.EvidenceId <= 0)
        return Results.BadRequest(new { error = "evidenceId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    var polarity = Revu.Core.Data.Repositories.EvidencePolarities.Normalize(body.Polarity);
    await w.Evidence.UpdatePolarityAsync(body.EvidenceId, polarity);
    // A polarity judgement promotes an untriaged row out of needs_review.
    await w.Evidence.UpdateStatusAsync(body.EvidenceId, Revu.Core.Data.Repositories.EvidenceStatuses.Evidence);
    log.LogInformation("Evidence {Id} polarity={Polarity}", body.EvidenceId, polarity);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/evidence/objective  { evidenceId, objectiveId? }  (null detaches)
// Mirrors ReviewViewModel.SetEvidenceObjectiveAsync: attach to an objective; when
// an objective is set, promote needs_review->evidence AND mark that objective
// practiced for this game (the evidence IS the proof the objective was practiced).
app.MapPost("/api/evidence/objective", async (EvidenceObjectiveBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.EvidenceId <= 0)
        return Results.BadRequest(new { error = "evidenceId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    long? objectiveId = (body.ObjectiveId is > 0) ? body.ObjectiveId : null;
    await w.Evidence.UpdateObjectiveAsync(body.EvidenceId, objectiveId);
    if (objectiveId is long oid && body.GameId is > 0)
    {
        // Promote out of needs_review and mark the objective practiced this game,
        // preserving any existing execution note (mirror MarkObjectivePracticed-
        // FromEvidenceAsync: only flips practiced->true, never clobbers the note).
        await w.Evidence.UpdateStatusAsync(body.EvidenceId, Revu.Core.Data.Repositories.EvidenceStatuses.Evidence);
        var existing = await w.Objectives.GetGameObjectivesAsync(body.GameId.Value);
        var note = existing.FirstOrDefault(r => r.ObjectiveId == oid)?.ExecutionNote ?? "";
        await w.Objectives.RecordGameAsync(body.GameId.Value, oid, practiced: true, executionNote: note);
    }
    log.LogInformation("Evidence {Id} -> objective {ObjectiveId}", body.EvidenceId, objectiveId);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/evidence/status  { evidenceId, status }  (needs_review|evidence|dismissed|highlight)
// Mirrors ReviewViewModel.SetEvidenceStatusAsync (dismiss is the common case).
app.MapPost("/api/evidence/status", async (EvidenceStatusBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.EvidenceId <= 0)
        return Results.BadRequest(new { error = "evidenceId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    var status = Revu.Core.Data.Repositories.EvidenceStatuses.Normalize(body.Status);
    await w.Evidence.UpdateStatusAsync(body.EvidenceId, status);
    log.LogInformation("Evidence {Id} status={Status}", body.EvidenceId, status);
    return Results.Json(new { ok = true }, jsonOptions);
});

// ── DEATH AUDIT (per-death cause classification) ──────────────────────────────
// POST /api/death/classify  { gameId, timeS, key }  — one-tap cause chip.
// Upsert keyed on (gameId, gameTimeSeconds); the repo normalizes the class key.
app.MapPost("/api/death/classify", async (DeathClassifyBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.GameId <= 0 || string.IsNullOrWhiteSpace(body.Key))
        return Results.BadRequest(new { error = "gameId and key required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.DeathClassifications.UpsertAsync(body.GameId, body.TimeS, body.Key.Trim());
    log.LogInformation("Death classified: game {GameId} @{TimeS}s -> {Key}", body.GameId, body.TimeS, body.Key);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/death/clear  { gameId, timeS }  — re-tapping the selected chip clears.
app.MapPost("/api/death/clear", async (DeathClearBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.DeathClassifications.ClearAsync(body.GameId, body.TimeS);
    log.LogInformation("Death classification cleared: game {GameId} @{TimeS}s", body.GameId, body.TimeS);
    return Results.Json(new { ok = true }, jsonOptions);
});

// ── CUSTOM PROMPT ANSWERS (per objective) ─────────────────────────────────────
// POST /api/prompt/answer/save  { promptId, gameId, text }  — upsert; empty text
// deletes the answer row (IPromptsRepository.SaveAnswerAsync handles both).
app.MapPost("/api/prompt/answer/save", async (PromptAnswerBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.PromptId <= 0 || body.GameId <= 0)
        return Results.BadRequest(new { error = "promptId and gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Prompts.SaveAnswerAsync(body.PromptId, body.GameId, body.Text ?? "");
    log.LogInformation("Prompt {PromptId} answer saved for game {GameId}", body.PromptId, body.GameId);
    return Results.Json(new { ok = true }, jsonOptions);
});

// ── FOCUS ADHERENCE (Yes/Partly/No, immediate persist) ────────────────────────
// POST /api/focus-adherence  { gameId, value? }  — value 2=Yes / 1=Partly / 0=No;
// null (or omitted) clears it back to unset (-1 semantics). Persisted immediately;
// also re-stamped at full save via Snapshot.FocusAdherence.
app.MapPost("/api/focus-adherence", async (FocusAdherenceBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.SessionLog.UpdateFocusAdherenceAsync(body.GameId, body.Value);
    log.LogInformation("Focus adherence for game {GameId} = {Value}", body.GameId, body.Value);
    return Results.Json(new { ok = true }, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// VOD BOOKMARK CRUD (Batch 2). The VOD player's Quick Bookmark tool + the
// bookmark-list edit/delete/tag/quality actions. These are PLAIN note-bookmark
// writes — clip extraction (ffmpeg) is DEFERRED to Batch 3, so AddBookmarkAsync
// is never passed clip ranges/paths here. Each reuses an IVodRepository method
// verbatim. All token-gated + backup-guarded.
// ─────────────────────────────────────────────────────────────────────────────

// POST /api/bookmark/add  { gameId, timeS, note?, objectiveId?, promptId? }
// Quick note-bookmark at the current video time (B key / Add button). Returns the
// new bookmark id so the frontend can optimistically render the row. Mirrors
// VodPlayerViewModel.AddBookmarkCommand (sans the clip fields).
app.MapPost("/api/bookmark/add", async (AddBookmarkBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    var id = await w.Vod.AddBookmarkAsync(
        gameId: body.GameId,
        gameTimeSeconds: body.TimeS,
        note: body.Note ?? "",
        objectiveId: body.ObjectiveId,
        promptId: body.PromptId);
    log.LogInformation("Bookmark added for game {GameId} @{TimeS}s -> id {Id}", body.GameId, body.TimeS, id);
    return Results.Json(new { ok = true, id }, jsonOptions);
});

// POST /api/bookmark/note  { bookmarkId, note }  — edit a bookmark's note.
app.MapPost("/api/bookmark/note", async (BookmarkNoteBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.BookmarkId <= 0)
        return Results.BadRequest(new { error = "bookmarkId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Vod.UpdateBookmarkAsync(body.BookmarkId, note: body.Note ?? "");
    log.LogInformation("Bookmark {Id} note updated", body.BookmarkId);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/bookmark/delete  { bookmarkId }  — remove a bookmark.
app.MapPost("/api/bookmark/delete", async (BookmarkIdBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.BookmarkId <= 0)
        return Results.BadRequest(new { error = "bookmarkId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Vod.DeleteBookmarkAsync(body.BookmarkId);
    log.LogInformation("Bookmark {Id} deleted", body.BookmarkId);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/bookmark/objective  { bookmarkId, objectiveId? }  — attach/detach the
// objective tag (null detaches). Distinct from /tag: objective-only, no prompt.
app.MapPost("/api/bookmark/objective", async (BookmarkObjectiveBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.BookmarkId <= 0)
        return Results.BadRequest(new { error = "bookmarkId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Vod.SetBookmarkObjectiveAsync(body.BookmarkId, body.ObjectiveId);
    log.LogInformation("Bookmark {Id} objective={ObjectiveId}", body.BookmarkId, body.ObjectiveId);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/bookmark/tag  { bookmarkId, objectiveId?, promptId? }  — set objective
// + optional prompt atomically (pass both null to detach). Mirrors
// IVodRepository.SetBookmarkTagAsync.
app.MapPost("/api/bookmark/tag", async (BookmarkTagBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.BookmarkId <= 0)
        return Results.BadRequest(new { error = "bookmarkId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Vod.SetBookmarkTagAsync(body.BookmarkId, body.ObjectiveId, body.PromptId);
    log.LogInformation("Bookmark {Id} tag objective={ObjectiveId} prompt={PromptId}", body.BookmarkId, body.ObjectiveId, body.PromptId);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/bookmark/quality  { bookmarkId, quality }  — good|neutral|bad (or "").
app.MapPost("/api/bookmark/quality", async (BookmarkQualityBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || body.BookmarkId <= 0)
        return Results.BadRequest(new { error = "bookmarkId required" });
    await w.BackupGuard.EnsureBackedUpAsync();
    await w.Vod.UpdateBookmarkAsync(body.BookmarkId, quality: body.Quality ?? "");
    log.LogInformation("Bookmark {Id} quality={Quality}", body.BookmarkId, body.Quality);
    return Results.Json(new { ok = true }, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// CLIP EXTRACTION (Batch 3). ffmpeg clip export from the VOD player's Clip tool.
// Reuses IClipService.ExtractClipAsync (bundled-or-PATH ffmpeg, two-stage copy →
// re-encode) verbatim, then the SAME IVodRepository.AddBookmarkAsync + Evidence
// upsert the WinUI ExtractClipCommand runs. The .mp4 lands in ClipsFolder from
// config. Token-gated + backup-guarded.
// ─────────────────────────────────────────────────────────────────────────────

// POST /api/clip/extract  { gameId, vodPath, championName?, startTimeS, endTimeS,
//   note?, quality?, objectiveId?, promptId? }
// Mirrors VodPlayerViewModel.ExtractClipAsync EXACTLY: clamp/order the range,
// default the note to "Clip", extract via ffmpeg into ClipsFolder, then on success
// add a clip-backed bookmark (returns id), mark the objective practiced if tagged,
// and upsert the evidence row (polarity = quality or neutral; status = evidence
// when a quality is set, else needs_review). vodPath + championName ride in the
// body — the frontend has both from the loaded VOD snapshot, matching the VM which
// reads them from its loaded VodPath/ChampionName state. Returns the clip path +
// bookmark id so the frontend can confirm + refetch.
app.MapPost("/api/clip/extract", async (ExtractClipBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || body.GameId <= 0)
        return Results.BadRequest(new { error = "gameId required" });
    if (string.IsNullOrWhiteSpace(body.VodPath))
        return Results.BadRequest(new { error = "vodPath required" });

    // Order + clamp the range exactly like the VM (start = min, end = max).
    var startS = Math.Max(0, Math.Min(body.StartTimeS, body.EndTimeS));
    var endS = Math.Max(0, Math.Max(body.StartTimeS, body.EndTimeS));
    if (endS - startS < 1)
        return Results.BadRequest(new { error = "clip range must be at least 1s" });

    await w.BackupGuard.EnsureBackedUpAsync();

    var note = string.IsNullOrWhiteSpace(body.Note) ? "Clip" : body.Note.Trim();
    var quality = (body.Quality ?? "").Trim().ToLowerInvariant();
    var objectiveId = (body.ObjectiveId is > 0) ? body.ObjectiveId : null;
    var promptId = (body.PromptId is > 0) ? body.PromptId : null;

    // championName for the clip filename: prefer the body; fall back to the game.
    var champion = (body.ChampionName ?? "").Trim();
    if (champion.Length == 0)
    {
        var game = await w.Games.GetAsync(body.GameId);
        champion = game?.ChampionName ?? "";
    }

    var clipsFolder = w.Config.ClipsFolder;
    var clipPath = await w.Clips.ExtractClipAsync(body.VodPath, startS, endS, champion, clipsFolder);
    if (string.IsNullOrEmpty(clipPath))
    {
        log.LogWarning("Clip extract failed for game {GameId} ({StartS}-{EndS}s) — ffmpeg returned no output", body.GameId, startS, endS);
        return Results.Json(new { ok = false, error = "Clip save failed (is ffmpeg installed?)." }, jsonOptions, statusCode: 422);
    }

    var bookmarkId = await w.Vod.AddBookmarkAsync(
        gameId: body.GameId,
        gameTimeSeconds: startS,
        note: note,
        clipStartSeconds: startS,
        clipEndSeconds: endS,
        clipPath: clipPath,
        objectiveId: objectiveId,
        quality: quality,
        promptId: promptId);

    // Tagging the clip to an objective marks it practiced for this game (preserve
    // any existing execution note — only flip practiced->true, never clobber).
    if (objectiveId is long oid)
    {
        var existing = await w.Objectives.GetGameObjectivesAsync(body.GameId);
        var exNote = existing.FirstOrDefault(r => r.ObjectiveId == oid)?.ExecutionNote ?? "";
        await w.Objectives.RecordGameAsync(body.GameId, oid, practiced: true, executionNote: exNote);
    }

    // Evidence row (mirror VM): polarity = quality or neutral; a quality-tagged
    // clip is already a judgement (status=evidence), an untagged one needs review.
    await w.Evidence.UpsertAsync(new Revu.Core.Data.Repositories.EvidenceUpsert(
        GameId: body.GameId,
        SourceKind: Revu.Core.Data.Repositories.EvidenceKinds.Clip,
        SourceId: bookmarkId,
        SourceKey: $"clip:{bookmarkId}",
        StartTimeSeconds: startS,
        EndTimeSeconds: endS,
        Title: note,
        Note: note,
        ObjectiveId: objectiveId,
        Polarity: string.IsNullOrWhiteSpace(quality)
            ? Revu.Core.Data.Repositories.EvidencePolarities.Neutral
            : Revu.Core.Data.Repositories.EvidencePolarities.Normalize(quality),
        Status: string.IsNullOrWhiteSpace(quality)
            ? Revu.Core.Data.Repositories.EvidenceStatuses.NeedsReview
            : Revu.Core.Data.Repositories.EvidenceStatuses.Evidence));

    log.LogInformation("Clip extracted: game {GameId} {StartS}-{EndS}s -> {Path} (bookmark {Id})", body.GameId, startS, endS, clipPath, bookmarkId);
    return Results.Json(new { ok = true, clipPath, bookmarkId }, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// PATTERN REVIEW writes (Batch 3). Close out a cross-game pattern + per-moment
// note autosave (which silently clips the moment's window the first time). Both
// reuse IEvidenceRepository methods verbatim. Token-gated + backup-guarded.
// ─────────────────────────────────────────────────────────────────────────────

// POST /api/pattern/mark-reviewed  { patternKey, kind?, momentCount? }
// Mirrors PatternReviewViewModel.MarkReviewedAsync → IEvidenceRepository.
// MarkPatternReviewedAsync(patternKey, kind, momentCount). kind + momentCount ride
// in the body (the frontend has both from the loaded /api/patterns snapshot); when
// kind is absent it's resolved from the live pattern cards so an older frontend
// still closes the pattern out correctly.
app.MapPost("/api/pattern/mark-reviewed", async (MarkPatternReviewedBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.PatternKey))
        return Results.BadRequest(new { error = "patternKey required" });
    await w.BackupGuard.EnsureBackedUpAsync();

    var key = body.PatternKey.Trim();
    var kind = (body.Kind ?? "").Trim();
    var momentCount = body.MomentCount ?? 0;

    // Resolve kind/momentCount from the live cards if the frontend didn't send them.
    if (kind.Length == 0 || body.MomentCount is null)
    {
        var card = (await w.Evidence.GetPatternCardsAsync()).FirstOrDefault(c => c.PatternKey == key);
        if (card is not null)
        {
            if (kind.Length == 0) kind = card.Kind;
            if (body.MomentCount is null)
            {
                var moments = await w.Evidence.GetPatternMomentsAsync(card);
                momentCount = moments.Count;
            }
        }
    }

    await w.Evidence.MarkPatternReviewedAsync(key, kind, momentCount);
    log.LogInformation("Pattern reviewed: {Key} ({Kind}, {Count} moments)", key, kind, momentCount);
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/pattern/moment/note  { evidenceId, text, gameId?, championName?, vodPath?,
//   polarity?, startTimeS?, endTimeS? }
// Mirrors PatternReviewViewModel.FlushNoteAsync: always persist the note via
// UpdateNoteAsync; then — only when the note is non-empty AND the moment has a VOD
// AND no clip yet — silently extract a padded clip over the moment's window,
// add a clip bookmark, and PROMOTE this evidence row to be that clip
// (AttachClipToEvidenceAsync), so it shows as a saved clip rather than a duplicate.
// The padded window mirrors ClipWindowFor (lead 8s / trail 4s for single points).
// alreadyClipped lets the frontend suppress re-extraction (mirrors HasClip gate).
app.MapPost("/api/pattern/moment/note", async (PatternMomentNoteBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || body.EvidenceId <= 0)
        return Results.BadRequest(new { error = "evidenceId required" });
    await w.BackupGuard.EnsureBackedUpAsync();

    var text = (body.Text ?? "").Trim();
    await w.Evidence.UpdateNoteAsync(body.EvidenceId, text);

    var clipped = false;
    string? clipPath = null;

    // Clip the moment's window once, only when there's a note + a playable VOD and
    // it hasn't been clipped yet (mirror the VM's clipNeeded guard).
    var hasVod = !string.IsNullOrWhiteSpace(body.VodPath);
    if (text.Length > 0 && hasVod && !body.AlreadyClipped && body.GameId is > 0)
    {
        try
        {
            var start = Math.Max(0, body.StartTimeS ?? 0);
            var end = body.EndTimeS ?? 0;
            // Padded window: real range when end>start, else lead/trail around point.
            var (startS, endS) = end > start
                ? (start, end)
                : (Math.Max(0, start - PatternClipLeadSeconds), start + PatternClipTrailSeconds);

            var note = text.Length > 0 ? text : (body.Title ?? "Clip");
            var quality = Revu.Core.Data.Repositories.EvidencePolarities.Normalize(body.Polarity);
            clipPath = await w.Clips.ExtractClipAsync(
                body.VodPath!, startS, endS, body.ChampionName ?? "", w.Config.ClipsFolder);

            if (!string.IsNullOrEmpty(clipPath))
            {
                var bookmarkId = await w.Vod.AddBookmarkAsync(
                    gameId: body.GameId.Value,
                    gameTimeSeconds: startS,
                    note: note,
                    clipStartSeconds: startS,
                    clipEndSeconds: endS,
                    clipPath: clipPath,
                    quality: quality);
                // Promote this moment's own evidence row to BE the clip.
                await w.Evidence.AttachClipToEvidenceAsync(body.EvidenceId, bookmarkId, startS, endS);
                clipped = true;
                log.LogInformation("Pattern moment {EvId} clipped: {StartS}-{EndS}s -> {Path}", body.EvidenceId, startS, endS, clipPath);
            }
        }
        catch (Exception ex)
        {
            // Note already saved — clip failure is non-fatal (mirror VM's try/catch).
            log.LogWarning(ex, "Pattern moment {EvId} note saved but clip failed", body.EvidenceId);
        }
    }

    return Results.Json(new { ok = true, clipped, clipPath }, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// SESSION LOG row write (Batch 2). Skip / delete / intention already exist
// (/api/review/skip, /api/game/delete, /api/block/start). This adds the one
// missing session-row action: clearing a false-positive rule-break flag.
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
// MANUAL GAME ENTRY (Batch 2). Hand-log a game NOT auto-captured + a minimal
// review. The Manual Entry page needs the active post-game objectives to render
// its practiced/note rows, then POSTs the whole form.
// ─────────────────────────────────────────────────────────────────────────────

// GET /api/objectives/active — active objectives that show in post-game (pre/in/
// post), shaped for the Manual Entry objectives card. READ-only; no backup guard.
// Mirrors ManualEntryDialogViewModel.LoadObjectivesAsync (GetActiveAsync filtered
// by ObjectivePhases.ShowsInPostGame). Uses the WRITE-graph Objectives repo (read
// methods on it are still plain SELECTs).
app.MapGet("/api/objectives/active", async (WriteServices w, ILogger<Program> log) =>
{
    try
    {
        var active = await w.Objectives.GetActiveAsync();
        var rows = active
            .Where(o => Revu.Core.Data.Repositories.ObjectivePhases.ShowsInPostGame(o.Phase))
            .Select(o => new
            {
                objectiveId = o.Id,
                title = o.Title,
                phaseLabel = Revu.Core.Data.Repositories.ObjectivePhases.ToDisplayLabel(o.Phase),
            })
            .ToList();
        return Results.Json(new { ok = true, objectives = rows }, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogDebug(ex, "Active objectives load failed (degraded to empty)");
        return Results.Json(new { ok = true, objectives = Array.Empty<object>() }, jsonOptions);
    }
});

// POST /api/game/manual — write a manual game row + log it + record objective
// assessments. Replicates ManualEntryDialogViewModel.SaveAsync EXACTLY:
//   (1) IGameRepository.SaveManualAsync(...) -> gameId
//   (2) IF gameId>0: ISessionLogRepository.LogGameAsync(gameId, champ, win, mental)
//   (3) foreach objective: IObjectivesRepository.RecordGameAsync(gameId, objId,
//       practiced, executionNote)
// MentalRating reaches LogGameAsync only (NOT SaveManualAsync), matching the VM.
app.MapPost("/api/game/manual", async (ManualGameBody body, WriteServices w, ILogger<Program> log) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.ChampionName))
        return Results.BadRequest(new { error = "championName required" });
    await w.BackupGuard.EnsureBackedUpAsync();

    var champ = body.ChampionName.Trim();
    var gameMode = string.IsNullOrWhiteSpace(body.GameMode) ? "Manual Entry" : body.GameMode.Trim();

    var gameId = await w.Games.SaveManualAsync(
        championName: champ,
        win: body.Win,
        kills: Math.Max(0, body.Kills),
        deaths: Math.Max(0, body.Deaths),
        assists: Math.Max(0, body.Assists),
        gameMode: gameMode,
        notes: (body.Notes ?? "").Trim(),
        mistakes: (body.Mistakes ?? "").Trim(),
        wentWell: (body.WentWell ?? "").Trim(),
        focusNext: (body.FocusNext ?? "").Trim());

    if (gameId > 0)
    {
        var mental = Math.Clamp(body.MentalRating <= 0 ? 5 : body.MentalRating, 1, 10);
        await w.SessionLog.LogGameAsync(gameId, champ, body.Win, mental);

        foreach (var o in (body.Objectives ?? new List<ManualObjectiveBody>()))
        {
            if (o is null || o.ObjectiveId <= 0) continue;
            await w.Objectives.RecordGameAsync(gameId, o.ObjectiveId, o.Practiced, o.ExecutionNote ?? "");
        }
    }

    log.LogInformation("Manual game entry saved: {Champion} ({Result}), game_id={GameId}",
        champ, body.Win ? "Win" : "Loss", gameId);
    return Results.Json(new { ok = true, gameId }, jsonOptions);
});

// ─────────────────────────────────────────────────────────────────────────────
// RIOT AUTH / ACCOUNT (Batch 4). SECURITY-SENSITIVE: the email-OTP login flow
// (mirrors OnboardingViewModel + SettingsViewModel's RiotAuth* commands) and the
// account resolve that fills RiotPuuid + auto-detected rank. The session triplet
// (RiotSessionToken/Email/ExpiresAt) and PUUID persist via the WRITE-graph
// IConfigService, which round-trips through the DPAPI IProtectedSecretStore — so
// these MUST run on WriteServices, never the read graph.
//
// These are PROXY calls to the Cloudflare Worker (IRiotAuthClient), not DB writes,
// so most don't take the BackupGuard; the two that DO persist config (/verify,
// /resolve, /logout) take it before the SaveAsync — belt-and-suspenders, matching
// every other write endpoint. RiotAuthException carries a user-displayable message
// (server-driven: invalid_email / invalid_or_expired_code / 429 / 401 / 502 …);
// we surface it with HTTP 422 so the Tauri layer's error-extraction shows it.
// ─────────────────────────────────────────────────────────────────────────────

// POST /api/auth/login  { email } — sends the magic-link / OTP email. No DB write.
// Mirrors OnboardingViewModel.SendLoginCodeAsync (validates non-empty email).
app.MapPost("/api/auth/login", async (AuthLoginBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.Email))
        return Results.BadRequest(new { error = "Enter an email to continue." });
    try
    {
        await w.RiotAuth.LoginAsync(body.Email.Trim(), ct);
        log.LogInformation("Auth: login code sent.");
        return Results.Json(new { ok = true, info = $"Check {body.Email.Trim()} for a code." }, jsonOptions);
    }
    catch (RiotAuthException ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Auth: login failed");
        return Results.Json(new { ok = false, error = "Couldn't reach the server. Check your connection." }, jsonOptions, statusCode: 502);
    }
});

// POST /api/auth/signup  { email, inviteCode } — sends the email after validating
// an invite code. Mirrors SettingsViewModel.RiotAuthSignup (both required; code
// upper-cased). No DB write.
app.MapPost("/api/auth/signup", async (AuthSignupBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.InviteCode))
        return Results.BadRequest(new { error = "Enter an email and an invite code." });
    try
    {
        await w.RiotAuth.SignupAsync(body.Email.Trim(), body.InviteCode.Trim().ToUpperInvariant(), ct);
        log.LogInformation("Auth: signup code sent.");
        return Results.Json(new { ok = true, info = $"Check {body.Email.Trim()} for a code." }, jsonOptions);
    }
    catch (RiotAuthException ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Auth: signup failed");
        return Results.Json(new { ok = false, error = "Couldn't reach the server. Check your connection." }, jsonOptions, statusCode: 502);
    }
});

// POST /api/auth/verify  { code } — exchanges the OTP for a session token and
// PERSISTS the session triplet (token/email/expiresAt) via config (DPAPI). Mirrors
// OnboardingViewModel.VerifyAsync + SettingsViewModel.RiotAuthVerify. The email we
// stamp is the one the verify call was issued for — the frontend passes it back so
// we record the right RiotSessionEmail (the proxy's verify response carries only
// the token + expiry). Code is trimmed + upper-cased (mirror the VM).
app.MapPost("/api/auth/verify", async (AuthVerifyBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.Code))
        return Results.BadRequest(new { error = "Paste the code from your email." });
    try
    {
        var result = await w.RiotAuth.VerifyAsync(body.Code.Trim().ToUpperInvariant(), ct);
        await w.BackupGuard.EnsureBackedUpAsync();

        var cfg = await w.Config.LoadAsync();
        cfg.RiotSessionToken = result.SessionToken;
        // Prefer the email the frontend carried over from the login step; fall back
        // to whatever's already stored so a re-verify doesn't blank it.
        if (!string.IsNullOrWhiteSpace(body.Email)) cfg.RiotSessionEmail = body.Email.Trim();
        cfg.RiotSessionExpiresAt = result.ExpiresAt;
        await w.Config.SaveAsync(cfg);

        log.LogInformation("Auth: session verified + persisted (expires {ExpiresAt}).", result.ExpiresAt);
        return Results.Json(new { ok = true, email = cfg.RiotSessionEmail }, jsonOptions);
    }
    catch (RiotAuthException ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Auth: verify failed");
        return Results.Json(new { ok = false, error = "Couldn't verify the code." }, jsonOptions, statusCode: 502);
    }
});

// POST /api/auth/resolve  { riotId, region } — resolve the Riot ID to a PUUID
// using the stored session token, persist RiotId/RiotRegion/RiotPuuid +
// OnboardingSkipped=false, then best-effort auto-detect the ranked solo tier
// (display-only, never throws). Mirrors OnboardingViewModel.FinishAccountAsync +
// SettingsViewModel.Save's PUUID-resolution leg. Validates the gameName#tagLine
// shape exactly like the VM. Returns the detected rank so the frontend can show
// the "RANK DETECTED" line.
app.MapPost("/api/auth/resolve", async (AuthResolveBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    var riotId = (body?.RiotId ?? "").Trim();
    var region = (body?.Region ?? "").Trim();
    // Mirror the VM validation: must contain '#', not start/end with it.
    if (!riotId.Contains('#') || riotId.StartsWith('#') || riotId.EndsWith('#'))
        return Results.BadRequest(new { error = "Enter your Riot ID as gameName#tagLine." });
    if (region.Length == 0)
        return Results.BadRequest(new { error = "Pick a region." });

    var cfg = await w.Config.LoadAsync();
    if (string.IsNullOrWhiteSpace(cfg.RiotSessionToken))
        return Results.Json(new { ok = false, error = "Session missing. Start over.", needsLogin = true }, jsonOptions, statusCode: 401);

    try
    {
        var regionLower = region.ToLowerInvariant();
        var account = await w.RiotAuth.ResolveAccountAsync(cfg.RiotSessionToken, riotId, regionLower, ct);

        await w.BackupGuard.EnsureBackedUpAsync();
        cfg.RiotId = riotId;
        cfg.RiotRegion = regionLower;
        cfg.RiotPuuid = account.Puuid;
        cfg.OnboardingSkipped = false; // login path: rely on RiotProxyEnabled + role
        await w.Config.SaveAsync(cfg);

        // Best-effort rank detection (display-only; never throws).
        var rank = await w.RiotAuth.GetSoloRankAsync(cfg.RiotSessionToken, account.Puuid, regionLower, ct);

        log.LogInformation("Auth: account resolved (puuid set, rank='{Rank}').", rank);
        return Results.Json(new { ok = true, puuid = account.Puuid, gameName = account.GameName, tagLine = account.TagLine, rank }, jsonOptions);
    }
    catch (RiotAuthException ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Auth: account resolve failed");
        return Results.Json(new { ok = false, error = "Couldn't validate that account." }, jsonOptions, statusCode: 502);
    }
});

// POST /api/auth/logout — clear the session triplet (and reset OnboardingSkipped so
// a stale opt-out doesn't trap the user), then best-effort tell the server. Mirrors
// SettingsViewModel.RiotAuthLogout: clear config FIRST (so the local state is gone
// even if the network call fails), then call LogoutAsync(token).
app.MapPost("/api/auth/logout", async (WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    await w.BackupGuard.EnsureBackedUpAsync();
    var cfg = await w.Config.LoadAsync();
    var token = cfg.RiotSessionToken;
    cfg.RiotSessionToken = "";
    cfg.RiotSessionEmail = "";
    cfg.RiotSessionExpiresAt = 0;
    cfg.OnboardingSkipped = false;
    await w.Config.SaveAsync(cfg);

    if (!string.IsNullOrWhiteSpace(token))
    {
        try { await w.RiotAuth.LogoutAsync(token, ct); }
        catch (Exception ex) { log.LogDebug(ex, "Auth: server logout failed (local session already cleared)"); }
    }
    log.LogInformation("Auth: logged out (session cleared).");
    return Results.Json(new { ok = true }, jsonOptions);
});

// POST /api/auth/clear-partial — blank a half-saved session (token saved but no
// RiotId) to avoid jamming the onboarding gate. Mirrors OnboardingViewModel.
// ClearPartialSessionAsync EXACTLY: no-op early-return when token+email empty AND
// expiresAt==0; otherwise blank all three and save. Called by the onboarding Back
// buttons. Wrapped so a failure is non-fatal (mirror the VM try/catch).
app.MapPost("/api/auth/clear-partial", async (WriteServices w, ILogger<Program> log) =>
{
    try
    {
        var cfg = await w.Config.LoadAsync();
        if (string.IsNullOrEmpty(cfg.RiotSessionToken) && string.IsNullOrEmpty(cfg.RiotSessionEmail) && cfg.RiotSessionExpiresAt == 0)
            return Results.Json(new { ok = true, cleared = false }, jsonOptions);

        await w.BackupGuard.EnsureBackedUpAsync();
        cfg.RiotSessionToken = "";
        cfg.RiotSessionEmail = "";
        cfg.RiotSessionExpiresAt = 0;
        await w.Config.SaveAsync(cfg);
        log.LogInformation("Auth: partial session cleared.");
        return Results.Json(new { ok = true, cleared = true }, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Auth: could not clear partial onboarding session");
        return Results.Json(new { ok = false, error = ex.Message }, jsonOptions);
    }
});

// GET /api/auth/status — the signed-in snapshot the Onboarding/Settings/VOD-share
// surfaces read: signed-in (token present + unexpired), the linked Riot ID/region/
// email, whether a PUUID is resolved (gates backfill), and the persisted role. Read
// from the WRITE-graph config via a fresh LoadAsync so it reflects the writes the
// auth POSTs above just made (the read-graph config is a separate cache). Secrets
// (token/PUUID value) are NOT returned — only the booleans derived from them.
app.MapGet("/api/auth/status", async (WriteServices w, ILogger<Program> log) =>
{
    try
    {
        var cfg = await w.Config.LoadAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedIn = !string.IsNullOrWhiteSpace(cfg.RiotSessionToken) && cfg.RiotSessionExpiresAt > now;
        return Results.Json(new
        {
            ok = true,
            signedIn,
            email = cfg.RiotSessionEmail ?? "",
            riotId = cfg.RiotId ?? "",
            region = cfg.RiotRegion ?? "",
            hasPuuid = !string.IsNullOrWhiteSpace(cfg.RiotPuuid),
            primaryRole = cfg.PrimaryRole ?? "",
            // backfill is usable only when signed in AND a PUUID + region are set.
            backfillReady = signedIn && !string.IsNullOrWhiteSpace(cfg.RiotPuuid) && !string.IsNullOrWhiteSpace(cfg.RiotRegion),
        }, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Auth: status read failed");
        return Results.Json(new { ok = true, signedIn = false, email = "", riotId = "", region = "", hasPuuid = false, primaryRole = "", backfillReady = false }, jsonOptions);
    }
});

// ─────────────────────────────────────────────────────────────────────────────
// CLIP SHARE (Batch 4). Upload a saved clip to the Revu backend so it's publicly
// viewable at revu.lol/<id>, then persist that URL on the bookmark. Mirrors
// VodPlayerViewModel.ShareClipAsync: needs a logged-in session token; enforces the
// 90-second duration cap; if already shared, returns the existing URL (no re-
// upload). On an Unauthorized upload (expired/invalid session) we CLEAR the stored
// session so the frontend can re-prompt login. The clip path + champion + duration
// are resolved SERVER-SIDE from the bookmark (the frontend only sends gameId +
// bookmarkId) — never hand-rolled. Token-gated + backup-guarded.
// ─────────────────────────────────────────────────────────────────────────────
const int MaxShareDurationSeconds = 90;

app.MapPost("/api/clip/upload", async (ShareClipBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    if (body is null || body.GameId <= 0 || body.BookmarkId <= 0)
        return Results.BadRequest(new { error = "gameId and bookmarkId required" });

    // Resolve the bookmark server-side: its clip path, share URL, and clip window.
    VodBookmarkRecord? bm;
    try
    {
        var marks = await w.Vod.GetBookmarksAsync(body.GameId);
        bm = marks.FirstOrDefault(m => m.Id == body.BookmarkId);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Share: bookmark lookup failed for game {GameId} bm {BookmarkId}", body.GameId, body.BookmarkId);
        return Results.Json(new { ok = false, error = "Couldn't load that clip." }, jsonOptions, statusCode: 422);
    }
    if (bm is null)
        return Results.Json(new { ok = false, error = "Clip not found." }, jsonOptions, statusCode: 404);

    // Already shared → return the existing URL (mirror the VM: no re-upload).
    if (!string.IsNullOrWhiteSpace(bm.ShareUrl))
        return Results.Json(new { ok = true, shareUrl = bm.ShareUrl, alreadyShared = true }, jsonOptions);

    if (string.IsNullOrWhiteSpace(bm.ClipPath) || !File.Exists(bm.ClipPath))
        return Results.Json(new { ok = false, error = "Clip file not found on disk." }, jsonOptions, statusCode: 422);

    // Duration cap (mirror MaxShareDurationSeconds=90). Use the stored clip window.
    var durationSeconds = (bm.ClipEndSeconds.HasValue && bm.ClipStartSeconds.HasValue)
        ? Math.Max(0, bm.ClipEndSeconds.Value - bm.ClipStartSeconds.Value)
        : 0;
    if (durationSeconds > MaxShareDurationSeconds)
        return Results.Json(new { ok = false, error = "Clips can be up to 90s — trim and re-clip." }, jsonOptions, statusCode: 422);

    // Logged-in check (the upload also enforces this, but we want the clear-session
    // + re-prompt behavior on a missing/expired token, not a generic error).
    var cfg = await w.Config.LoadAsync();
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var signedIn = !string.IsNullOrWhiteSpace(cfg.RiotSessionToken) && cfg.RiotSessionExpiresAt > now;
    if (!signedIn)
        return Results.Json(new { ok = false, error = "You need to be logged in to share clips.", needsLogin = true }, jsonOptions, statusCode: 401);

    await w.BackupGuard.EnsureBackedUpAsync();

    // champion for the watch page: prefer the body, else the game row.
    var champion = (body.ChampionName ?? "").Trim();
    if (champion.Length == 0)
    {
        var game = await w.Games.GetAsync(body.GameId);
        champion = game?.ChampionName ?? "";
    }
    var title = string.IsNullOrWhiteSpace(body.Title) ? (bm.Note ?? "") : body.Title!.Trim();

    try
    {
        var result = await w.ClipUpload.UploadAsync(
            filePath: bm.ClipPath,
            sessionToken: cfg.RiotSessionToken,
            title: title,
            champion: champion,
            durationSeconds: durationSeconds > 0 ? durationSeconds : (int?)null,
            progress: null,
            ct: ct);

        await w.Vod.SetBookmarkShareUrlAsync(body.BookmarkId, result.Url);
        log.LogInformation("Clip shared: bm {BookmarkId} -> {Url}", body.BookmarkId, result.Url);
        return Results.Json(new { ok = true, shareUrl = result.Url, alreadyShared = false }, jsonOptions);
    }
    catch (ClipUploadException ex)
    {
        if (ex.Unauthorized)
        {
            // Expired/invalid session: clear it so the frontend re-prompts login.
            try
            {
                cfg.RiotSessionToken = "";
                cfg.RiotSessionExpiresAt = 0;
                await w.Config.SaveAsync(cfg);
            }
            catch (Exception clearEx) { log.LogDebug(clearEx, "Share: failed to clear rejected session"); }
            return Results.Json(new { ok = false, error = ex.Message, needsLogin = true }, jsonOptions, statusCode: 401);
        }
        return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Share: upload failed for bm {BookmarkId}", body.BookmarkId);
        return Results.Json(new { ok = false, error = "Couldn't upload the clip. Try again." }, jsonOptions, statusCode: 502);
    }
});

// ─────────────────────────────────────────────────────────────────────────────
// RIOT-API BACKFILL (Batch 4). Walk games missing enemy_laner / laning@10 and
// resolve them via Match-V5 (through the proxy). Long-running (throttled ~1.5 RPS,
// two round-trips per game on the laning leg). Mirrors SettingsViewModel.Backfill-
// EnemyLanersCommand: run EnemyLanerBackfillService.RunAsync, then the laning leg
// wrapped so a proxy 404 (no /timeline route yet) degrades silently. Bails to a
// zero result with a friendly note if not signed in / no PUUID (the services
// themselves return 0/0/0/0 in that case). This is fire-and-return: the whole run
// completes before the response (the 5-min sidecar request timeout covers small-
// to-moderate backlogs; a huge backlog can be re-run to drain the rest).
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/backfill/start", async (WriteServices w, ILogger<Program> log, CancellationToken ct) =>
{
    var cfg = await w.Config.LoadAsync();
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var signedIn = !string.IsNullOrWhiteSpace(cfg.RiotSessionToken) && cfg.RiotSessionExpiresAt > now;
    if (!signedIn || string.IsNullOrWhiteSpace(cfg.RiotPuuid) || string.IsNullOrWhiteSpace(cfg.RiotRegion))
    {
        return Results.Json(new
        {
            ok = true,
            ranBackfill = false,
            text = "Sign in and link your Riot ID first — backfill needs your account.",
            enemy = new { scanned = 0, updated = 0, skipped = 0, failed = 0 },
            laning = new { scanned = 0, updated = 0, skipped = 0, failed = 0 },
        }, jsonOptions);
    }

    await w.BackupGuard.EnsureBackedUpAsync();

    EnemyLanerBackfillResult enemy;
    try
    {
        enemy = await w.EnemyLanerBackfill.RunAsync(ct: ct);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Backfill: enemy-laner leg failed");
        return Results.Json(new { ok = false, error = $"Backfill failed: {ex.Message}" }, jsonOptions, statusCode: 502);
    }

    // Laning leg degrades silently on a proxy 404 (mirror the VM try/catch).
    LaningBackfillResult laning = new(0, 0, 0, 0);
    try
    {
        laning = await w.LaningBackfill.RunAsync(ct: ct);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        log.LogDebug(ex, "Backfill: laning leg failed (degraded, non-fatal)");
    }

    var totalUpdated = enemy.Updated + laning.Updated;
    var text = totalUpdated > 0
        ? $"Backfilled {totalUpdated} game(s). Enemy laners: {enemy.Updated}/{enemy.Scanned}. Laning@10: {laning.Updated}/{laning.Scanned}."
        : "Nothing to backfill — every game already has its matchup data.";

    log.LogInformation("Backfill done: enemy {EU}/{ES}, laning {LU}/{LS}", enemy.Updated, enemy.Scanned, laning.Updated, laning.Scanned);
    return Results.Json(new
    {
        ok = true,
        ranBackfill = true,
        text,
        enemy = new { scanned = enemy.Scanned, updated = enemy.Updated, skipped = enemy.Skipped, failed = enemy.Failed },
        laning = new { scanned = laning.Scanned, updated = laning.Updated, skipped = laning.Skipped, failed = laning.Failed },
    }, jsonOptions);
});

// ── On Started: read the real bound port and publish {port,token} ────────────
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var programLogger = app.Services.GetRequiredService<ILogger<Program>>();
lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var addressesFeature = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

        var address = addressesFeature?.Addresses.FirstOrDefault() ?? "";
        var port = ExtractPort(address);

        WriteSidecarHandshake(port, bearerToken, programLogger);
        programLogger.LogInformation("Revu sidecar listening on 127.0.0.1:{Port}", port);
    }
    catch (Exception ex)
    {
        programLogger.LogError(ex, "Failed to publish sidecar handshake file");
    }

    // P-022: auto-match Ascent recordings to unlinked games at startup, so a present
    // recording attaches WITHOUT a manual Settings → Scan. In the WinUI→Tauri port the
    // matcher was left wired only to the manual scan button, so a freshly-played game's
    // VOD never showed until the user scanned by hand. Runs once, fire-and-forget,
    // behind the session backup guard (same as POST /api/settings/scan-vods); fully
    // idempotent (AutoMatchRecordingsAsync skips already-linked games + taken paths)
    // and swallowed so it can never disturb startup. The per-game heal on game-end
    // (SidecarGameFlowCoordinator) covers recordings that finalise after this runs.
    _ = Task.Run(async () =>
    {
        try
        {
            var write = app.Services.GetRequiredService<WriteServices>();
            await write.BackupGuard.EnsureBackedUpAsync();
            var matched = await write.VodScan.AutoMatchRecordingsAsync();
            if (matched > 0)
                programLogger.LogInformation("Startup VOD auto-match linked {Matched} recording(s)", matched);
        }
        catch (Exception ex)
        {
            programLogger.LogDebug(ex, "Startup VOD auto-match skipped (non-fatal)");
        }
    });
});

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// Helpers (top-level local functions)
// ─────────────────────────────────────────────────────────────────────────────

static bool TryGetBearer(string? authorizationHeader, out byte[] tokenBytes)
{
    tokenBytes = Array.Empty<byte>();
    if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;

    const string prefix = "Bearer ";
    if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

    var token = authorizationHeader.Substring(prefix.Length).Trim();
    if (token.Length == 0) return false;

    tokenBytes = Encoding.UTF8.GetBytes(token);
    return true;
}

static bool FixedTimeTokenEquals(byte[] expected, byte[] presented)
{
    // Length check first would leak length via timing, but CryptographicOperations
    // .FixedTimeEquals requires equal lengths. Compare against a fixed-size hash of
    // each side so mismatched lengths still take constant time.
    Span<byte> expectedHash = stackalloc byte[32];
    Span<byte> presentedHash = stackalloc byte[32];
    SHA256.HashData(expected, expectedHash);
    SHA256.HashData(presented, presentedHash);
    return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
}

static int ExtractPort(string address)
{
    if (string.IsNullOrWhiteSpace(address)) return 0;
    if (Uri.TryCreate(address, UriKind.Absolute, out var uri)) return uri.Port;
    return 0;
}

static void WriteSidecarHandshake(int port, string token, ILogger logger)
{
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var dir = Path.Combine(localAppData, "Revu");
    Directory.CreateDirectory(dir); // sidecar handshake dir — NOT the DB data dir.

    var file = Path.Combine(dir, "sidecar.json");
    var payload = JsonSerializer.Serialize(new { port, token });

    // Best-effort lock-down: write then restrict to current user on Windows.
    File.WriteAllText(file, payload);
    logger.LogInformation("Wrote sidecar handshake to {File}", file);
}

// ─────────────────────────────────────────────────────────────────────────────
// Objective write helpers (shared by /api/objective/create + /update).
// ─────────────────────────────────────────────────────────────────────────────

// Mirror ObjectivesViewModel: type-select index/string → the persisted column.
static string NormalizeObjectiveType(string? type)
{
    var t = (type ?? "").Trim().ToLowerInvariant();
    return t switch
    {
        "mini" or "2" => "mini",
        "mental" or "1" => "mental",
        _ => "primary",
    };
}

// Persist the side-tables of an objective AFTER its core row exists, in the same
// order the WinUI VM does: prompts diff-save → champion gate replace → focus phase
// → structured criterion. Reuses Revu.Core write methods verbatim.
static async Task PersistObjectiveSideTablesAsync(
    Revu.Sidecar.WriteServices w,
    long objectiveId,
    List<ObjectivePromptBody>? prompts,
    List<string>? champions,
    int focusPhaseIndex,
    int criteriaMetricIndex,
    int criteriaOpIndex,
    string? criteriaValueText,
    List<string>? eventTypes = null)
{
    // ── Custom prompts: diff-save against the stored rows. Mirrors
    //    ObjectivesViewModel.SavePromptsForObjectiveAsync: blank labels never
    //    persist; unchanged rows skip the update; removed rows are deleted;
    //    sortOrder = the prompt's index in the submitted list.
    var draft = prompts ?? new List<ObjectivePromptBody>();
    var existing = await w.Prompts.GetPromptsForObjectiveAsync(objectiveId);
    var existingById = existing.ToDictionary(p => p.Id);
    var keptIds = new HashSet<long>();

    for (var i = 0; i < draft.Count; i++)
    {
        var label = (draft[i].Label ?? "").Trim();
        if (string.IsNullOrEmpty(label)) continue; // blank rows don't persist

        var phase = Revu.Core.Data.Repositories.ObjectivePhases.Normalize(draft[i].Phase);
        var originalId = draft[i].Id;

        if (originalId > 0 && existingById.TryGetValue(originalId, out var prior))
        {
            keptIds.Add(prior.Id);
            // Only write when something actually changed (avoid updated_at churn).
            if (prior.Phase != phase || prior.Label != label || prior.SortOrder != i)
            {
                await w.Prompts.UpdatePromptAsync(prior.Id, phase, label, i);
            }
        }
        else
        {
            var newId = await w.Prompts.CreatePromptAsync(objectiveId, phase, label, i);
            keptIds.Add(newId);
        }
    }

    // Anything on disk but not in the submitted list anymore was deleted.
    foreach (var prior in existing)
    {
        if (!keptIds.Contains(prior.Id))
        {
            await w.Prompts.DeletePromptAsync(prior.Id);
        }
    }

    // ── Champion gate: replace wholesale (empty list = applies to all champions).
    //    De-dupe case-insensitively, preserve caller casing (mirror AddChampion).
    var champs = new List<string>();
    foreach (var c in champions ?? new List<string>())
    {
        var name = (c ?? "").Trim();
        if (name.Length == 0) continue;
        if (champs.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
        champs.Add(name);
    }
    await w.Objectives.SetChampionsForObjectiveAsync(objectiveId, champs);

    // ── Event-token gate: replace wholesale (empty list = tracks no events). The
    //    repo validates each token against the trackable vocabulary, so junk is
    //    dropped silently; we just forward the submitted list. Wrapped defensively
    //    so a token-table problem (e.g. a DB that somehow missed the v8 migration)
    //    never fails the whole objective save — the objective + its other side
    //    tables still persist; only the event-token tie is skipped.
    try
    {
        await w.Objectives.SetEventTokensForObjectiveAsync(
            objectiveId, eventTypes ?? new List<string>());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[objective {objectiveId}] event-token persist failed (skipped): {ex.Message}");
    }

    // ── Auto-clip focus phase (0 Auto / 1 Laning / 2 Mid-late / 3 Teamfight / 4 Any).
    await w.Objectives.UpdateFocusPhaseAsync(
        objectiveId,
        Revu.Core.Data.Repositories.ObjectiveFocusPhases.FromIndex(focusPhaseIndex));

    // ── Structured criterion. Metric index 0 ("Free text only") clears it; 1..N
    //    map to ObjectiveCriteria.Metrics[index-1]. Op index 1 = "<=" else ">=".
    //    Value parses invariant first then current culture, defaults 0 (mirror VM).
    var metricKey =
        criteriaMetricIndex > 0 && criteriaMetricIndex <= Revu.Core.Services.ObjectiveCriteria.Metrics.Count
            ? Revu.Core.Services.ObjectiveCriteria.Metrics[criteriaMetricIndex - 1].Key
            : "";
    var valueText = (criteriaValueText ?? "").Trim();
    if (!double.TryParse(valueText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var criteriaValue))
    {
        double.TryParse(valueText, out criteriaValue);
    }
    await w.Objectives.UpdateCriteriaAsync(
        objectiveId,
        metricKey,
        criteriaOpIndex == 1 ? "<=" : ">=",
        criteriaValue);
}

// ── Write-endpoint request bodies ────────────────────────────────────────────
internal sealed record StartBlockBody(string Intention);
// Date is the open block's own date (from IntentDto.BlockDate) so a carried-over
// block from a prior day closes the right row. Null/empty falls back to today.
internal sealed record EndBlockBody(int Rating, string? Note, string? Date = null);
internal sealed record RestoreBackupBody(string BackupFilePath);
internal sealed record GameIdBody(long GameId);
internal sealed record ObjectiveIdBody(long Id);

// ── Review-page granular write bodies (Batch 2) ──────────────────────────────
// Shared evidence triage (Review + VOD): polarity, objective-attach, status.
internal sealed record EvidencePolarityBody(long EvidenceId, string? Polarity);
// ObjectiveId null/<=0 detaches; GameId (optional) lets attach also mark the
// objective practiced for that game (mirrors the WinUI evidence-attach flow).
internal sealed record EvidenceObjectiveBody(long EvidenceId, long? ObjectiveId, long? GameId);
internal sealed record EvidenceStatusBody(long EvidenceId, string? Status);
// Per-death cause classification, keyed on (gameId, timeS).
internal sealed record DeathClassifyBody(long GameId, int TimeS, string Key);
internal sealed record DeathClearBody(long GameId, int TimeS);
// Per-objective custom-prompt answer (empty Text deletes the row).
internal sealed record PromptAnswerBody(long PromptId, long GameId, string? Text);
// Per-game focus adherence: 2=Yes / 1=Partly / 0=No; null clears.
internal sealed record FocusAdherenceBody(long GameId, int? Value);

// ── VOD bookmark CRUD bodies (Batch 2) ───────────────────────────────────────
// Quick note-bookmark add (no clip fields — ffmpeg deferred to Batch 3).
internal sealed record AddBookmarkBody(long GameId, int TimeS, string? Note, long? ObjectiveId, long? PromptId);
internal sealed record BookmarkIdBody(long BookmarkId);
internal sealed record BookmarkNoteBody(long BookmarkId, string? Note);
internal sealed record BookmarkObjectiveBody(long BookmarkId, long? ObjectiveId);
internal sealed record BookmarkTagBody(long BookmarkId, long? ObjectiveId, long? PromptId);
internal sealed record BookmarkQualityBody(long BookmarkId, string? Quality);

// ── Manual game entry bodies (Batch 2) ───────────────────────────────────────
// One objective assessment row (practiced toggle + execution note).
internal sealed record ManualObjectiveBody(long ObjectiveId, bool Practiced, string? ExecutionNote);
// The whole Manual Entry form. MentalRating reaches LogGameAsync only.
internal sealed record ManualGameBody(
    string ChampionName,
    bool Win,
    int Kills,
    int Deaths,
    int Assists,
    int MentalRating,
    string? GameMode,
    string? Notes,
    string? Mistakes,
    string? WentWell,
    string? FocusNext,
    List<ManualObjectiveBody>? Objectives);

internal sealed record ObjectivePracticeBody(long ObjectiveId, bool Practiced, string? ExecutionNote);

internal sealed record SaveReviewBody(
    long GameId,
    string? ChampionName,
    bool Win,
    int MentalRating,
    string? WentWell,
    string? Mistakes,
    string? FocusNext,
    string? ReviewNotes,
    string? ImprovementNote,
    string? Attribution,
    string? MentalHandled,
    string? SpottedProblems,
    string? OutsideControl,
    string? WithinControl,
    string? PersonalContribution,
    string? EnemyLaner,
    string? MatchupNote,
    List<long>? SelectedTagIds,
    List<ObjectivePracticeBody>? ObjectivePractices,
    int? FocusAdherence);

// One custom-prompt row in a create/update objective body. Id 0/absent = a NEW
// prompt (insert); a non-zero Id refers to an existing row the diff-save updates
// (or, if absent from the list, deletes). Phase is "pregame"|"ingame"|"postgame".
internal sealed record ObjectivePromptBody(long Id, string? Phase, string? Label);

// POST /api/objective/create body. Beyond the core fields it now carries the full
// editing surface: custom prompts (diff-saved), champion gate (replaced wholesale,
// empty = all champs), focus-phase picker index, and the structured criterion
// (metric/op picker indices + value text). Mirrors ObjectivesViewModel form state.
internal sealed record CreateObjectiveBody(
    string Title, string? SkillArea, string? Type,
    string? CompletionCriteria, string? Description,
    bool PracticePre, bool PracticeIn, bool PracticePost,
    int TargetGameCount,
    List<ObjectivePromptBody>? Prompts,
    List<string>? Champions,
    int FocusPhaseIndex,
    int CriteriaMetricIndex,
    int CriteriaOpIndex,
    string? CriteriaValueText,
    // Trackable event tokens the objective is tied to (raw types, SPELL_*, TEAMFIGHT).
    List<string>? EventTypes = null);

// POST /api/objective/update body. Same editing surface as create, plus the id
// and the explicit target-game-count (minis only; primary/mental force 0).
internal sealed record UpdateObjectiveBody(
    long Id, string Title, string? SkillArea, string? Type,
    string? CompletionCriteria, string? Description,
    bool PracticePre, bool PracticeIn, bool PracticePost,
    int TargetGameCount,
    List<ObjectivePromptBody>? Prompts,
    List<string>? Champions,
    int FocusPhaseIndex,
    int CriteriaMetricIndex,
    int CriteriaOpIndex,
    string? CriteriaValueText,
    List<string>? EventTypes = null);

internal sealed record ResetBody(
    string Emotion, int IntensityBefore, int? IntensityAfter,
    string? ReframeThought, string? ReframeResponse, string? IfThenPlan);

// POST /api/config/save body. EVERY field is nullable: null = "leave unchanged"
// (read-modify-write only mutates the fields the frontend actually sends, so a
// partial save from the Settings page never clobbers unrelated config keys).
internal sealed record SaveConfigBody(
    string? AscentFolder,
    bool? AscentReminderDismissed,
    string? ClipsFolder,
    int? ClipsMaxSizeMb,
    bool? BackupEnabled,
    string? BackupFolder,
    bool? TiltFixMode,
    bool? RequireReviewNotes,
    bool? SidebarAnimationEnabled,
    bool? MinimizeDuringGame,
    bool? AutoTimelineClippingEnabled,
    bool? AutoTimelineClippingHintDismissed,
    string? RiotId,
    string? Region,
    string? PrimaryRole,
    // Onboarding role-finish (skip path) stamps OnboardingSkipped=true; null leaves
    // it unchanged. Login path never sends it (resolve already set it false).
    bool? OnboardingSkipped);

// POST /api/review/draft/save body — same shape as SaveReviewBody minus the
// championName/win/requireReviewNotes fields a finalized save needs (a draft
// only carries the gameId + the in-progress ReviewSnapshot).
internal sealed record SaveReviewDraftBody(
    long GameId,
    int MentalRating,
    string? WentWell,
    string? Mistakes,
    string? FocusNext,
    string? ReviewNotes,
    string? ImprovementNote,
    string? Attribution,
    string? MentalHandled,
    string? SpottedProblems,
    string? OutsideControl,
    string? WithinControl,
    string? PersonalContribution,
    string? EnemyLaner,
    string? MatchupNote,
    List<long>? SelectedTagIds,
    List<ObjectivePracticeBody>? ObjectivePractices,
    int? FocusAdherence);

// ── Rules CRUD request bodies ─────────────────────────────────────────────────
// RuleType is one of: custom | no_play_day | no_play_after | loss_streak |
// max_games | min_mental (null/blank → custom). ConditionValue is the raw stored
// value the per-type formatter reads; for loss_streak the frontend encodes the
// optional cooldown as "threshold:minutes". ReplacementPlan is the P2c "then I
// will…" plan (optional). All trimmed server-side.
internal sealed record RuleIdBody(long Id);

internal sealed record CreateRuleBody(
    string Name,
    string? RuleType,
    string? ConditionValue,
    string? Description,
    string? ReplacementPlan);

internal sealed record UpdateRuleBody(
    long Id,
    string Name,
    string? RuleType,
    string? ConditionValue,
    string? Description,
    string? ReplacementPlan);

// ── Clip extraction body (Batch 3) ───────────────────────────────────────────
// VodPath + ChampionName ride along from the loaded VOD snapshot (the frontend
// has both), mirroring the WinUI VM which reads them from its loaded state. The
// range is clamped/ordered server-side. Quality is good|neutral|bad (or blank).
internal sealed record ExtractClipBody(
    long GameId,
    string VodPath,
    string? ChampionName,
    int StartTimeS,
    int EndTimeS,
    string? Note,
    string? Quality,
    long? ObjectiveId,
    long? PromptId);

// ── Pattern review bodies (Batch 3) ───────────────────────────────────────────
// Mark a cross-game pattern reviewed. Kind + MomentCount come from the loaded
// /api/patterns snapshot; both optional so the server can re-resolve them.
internal sealed record MarkPatternReviewedBody(string PatternKey, string? Kind, int? MomentCount);

// Per-moment note autosave (+ silent one-time clip). EvidenceId + Text are
// required; the clip fields ride from the loaded snapshot. AlreadyClipped lets the
// frontend suppress re-extraction once a moment has a clip (mirrors HasClip gate).
internal sealed record PatternMomentNoteBody(
    long EvidenceId,
    string? Text,
    long? GameId,
    string? ChampionName,
    string? VodPath,
    string? Title,
    string? Polarity,
    int? StartTimeS,
    int? EndTimeS,
    bool AlreadyClipped);

// ── Riot auth / account bodies (Batch 4) ─────────────────────────────────────
// Email-OTP login. The proxy sends the code; nothing persists until /verify.
internal sealed record AuthLoginBody(string Email);
// Sign-up = login + an invite code (upper-cased server-side).
internal sealed record AuthSignupBody(string Email, string InviteCode);
// Verify the OTP and persist the session. Email rides along so we stamp the right
// RiotSessionEmail (the proxy's verify response carries only token + expiry).
internal sealed record AuthVerifyBody(string Code, string? Email);
// Resolve the Riot ID → PUUID using the stored session; persists id/region/puuid.
internal sealed record AuthResolveBody(string RiotId, string Region);

// ── Clip share body (Batch 4) ────────────────────────────────────────────────
// The frontend sends only gameId + bookmarkId (+ optional caption); the sidecar
// resolves the clip path / champion / share-state from the bookmark server-side.
internal sealed record ShareClipBody(long GameId, long BookmarkId, string? ChampionName, string? Title);

// ── Pre-game deferred-snapshot bodies (Batch 5 / LCU) ────────────────────────
// These stage champ-select choices into LcuLiveState; the SidecarGameFlowCoordinator
// persists them to session_log at game END (mirror the PreGameDialogViewModel
// statics → ShellViewModel EOG hop). The draft body is the ONE that writes to the
// DB (a pre_game_draft_prompts upsert under the live session key).
internal sealed record PreGameMoodBody(int Mood);
internal sealed record PreGameIntentBody(string? Intention, string? Source, bool Cleared);
internal sealed record PreGamePracticedBody(List<long>? ObjectiveIds);
internal sealed record PreGameDraftBody(long PromptId, string? Text);

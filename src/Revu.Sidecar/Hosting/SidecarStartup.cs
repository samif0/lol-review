#nullable enable

using Revu.Core.Data;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>Database initialization and launch publication, after exclusive data ownership is acquired.</summary>
public static class SidecarStartup
{
    public static SidecarHostSession AcquireSession(bool isolatedHostTest)
    {
        var overrideRoot = Environment.GetEnvironmentVariable("REVU_DATA_ROOT");
        if (isolatedHostTest && string.IsNullOrWhiteSpace(overrideRoot))
            throw new InvalidOperationException("Isolated host testing requires an explicit REVU_DATA_ROOT.");
        if (isolatedHostTest) IsolatedHostPolicy.ValidateRoot(overrideRoot!);
        var session = new SidecarHostSession(AppDataPaths.UserDataRoot,
            AppDataPaths.SidecarHandshakeDirectory, Environment.GetEnvironmentVariable("REVU_HOST_LAUNCH_ID"));
        try
        {
            if (isolatedHostTest) IsolatedHostPolicy.ValidateConfiguration(AppDataPaths.UserDataRoot);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public static async Task InitializeStorageAsync(this WebApplication app, bool isolatedHostTest)
    {
        var backgroundWork = app.Services.GetRequiredService<SidecarBackgroundWork>();
        // Create only genuinely missing databases, then apply additive migrations,
        // idempotent seeds, and indexes. Never invoke the normalize/rebuild path.
        // Preserve the existing degraded-start policy: failures are logged and
        // database-dependent endpoints report their own availability.
        try
        {
            var migrateLogger = app.Services.GetRequiredService<ILoggerFactory>();
            var writeFactory = new WriteSqliteConnectionFactory(
                migrateLogger.CreateLogger<WriteSqliteConnectionFactory>(), migrateLogger);

            // Missing-only: returns true only on a genuinely fresh install (no canonical
            // AND no legacy DB). On an existing DB this is a no-op — no recreation, no wipe.
            writeFactory.CreateFreshDatabaseIfMissing();

            if (File.Exists(writeFactory.DatabasePath))
            {
                var migrator = new DatabaseInitializer(
                    writeFactory, migrateLogger.CreateLogger<DatabaseInitializer>());
                await migrator.ApplyAdditiveSchemaAsync();

                // Seeds + post-migration indexes + objective count/score reconciliation.
                // The WinUI app's InitializeAsync used to run these on every launch; the
                // sidecar-only era lost them, which left fresh installs with no
                // persistent_notes row (notes never saved), no default derived events
                // (empty VOD timeline), no default concept tags, and no visible-games
                // indexes. Idempotent, non-destructive, safe on every startup.
                await migrator.ApplySeedsAndIndexesAsync();

                if (!isolatedHostTest)
                {
                    // Back-catalog account-scope repair: games captured before 3.1.6 (or by a
                    // stale sidecar) carry the UUID-shaped LCU localPlayer.puuid instead of the
                    // encrypted Riot PUUID our login stores, so the dashboard's account filter
                    // hides them ("GAMES 0 even though I played N"). GameService now reconciles
                    // this per-game at capture, but already-saved rows need a one-time sweep.
                    // Idempotent + scoped to non-empty mismatched rows + no-op when logged out,
                    // so it's safe to run on every startup. Resolved from the read graph's
                    // IConfigService (same config.json the write graph owns).
                    try
                    {
                        var cfg = await app.Services.GetRequiredService<IConfigService>().LoadAsync();
                        await migrator.ReconcileGamePuuidAsync(cfg.RiotPuuid);
                    }
                    catch (Exception reconcileEx)
                    {
                        migrateLogger.CreateLogger("Startup").LogWarning(
                            reconcileEx, "Back-catalog PUUID reconcile skipped (non-fatal)");
                    }

                    // v3.5: pattern-evidence window backfill — materialize the pattern-
                    // feeding evidence rows for window games not yet stamped at the current
                    // materializer version (games.pattern_evidence_v, map_state_v shape).
                    // Restores the Patterns page for DBs that predate the materializer, and
                    // lights the dashboard nag without the Patterns page ever opening.
                    // Fire-and-forget so a large window never delays startup; idempotent and
                    // self-healing (a mid-pass crash leaves games unstamped for next launch).
                    // GET /api/patterns itself stays strictly read-only.
                    backgroundWork.TryRun("startup pattern evidence", async () =>
                    {
                        try
                        {
                            var w = app.Services.GetRequiredService<WriteServices>();
                            // Only trip the one-time session backup when there is actual
                            // work — an every-launch backup would rotate the 3-slot safety
                            // pool on quiet starts.
                            var pending = await w.Games.GetPatternEvidenceBackfillIdsAsync(
                                Revu.Core.Services.PatternEvidenceMaterializer.Version);
                            if (pending.Count == 0) return;
                            await w.BackupGuard.EnsureBackedUpAsync();
                            await w.PatternMaterializer.BackfillWindowAsync();
                        }
                        catch (Exception backfillEx)
                        {
                            app.Services.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("Startup")
                                .LogError(backfillEx, "Pattern-evidence window backfill failed at startup");
                        }
                    });

                    // v3.11: event corrections sweep (rule G). Stamp event_key on rows that predate
                    // the ledger and let corrections whose applied row vanished find their twin
                    // again. Same shape as the backfill above: only trips the session backup when
                    // there is work, capped per launch, never inserts game_events rows.
                    backgroundWork.TryRun("startup event corrections", async () =>
                    {
                        try
                        {
                            var w = app.Services.GetRequiredService<WriteServices>();
                            var pending = await w.EventCorrectionSweep.CountPendingAsync();
                            if (pending == 0) return;
                            await w.BackupGuard.EnsureBackedUpAsync();
                            var result = await w.EventCorrectionSweep.RunAsync(maxRows: 20_000);
                            app.Services.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("Startup")
                                .LogInformation(
                                    "Event corrections sweep: {Keys} keys stamped, {Games} games touched, {Orphans} orphans resolved",
                                    result.KeysStamped, result.GamesTouched, result.OrphansResolved);
                        }
                        catch (Exception sweepEx)
                        {
                            app.Services.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("Startup")
                                .LogError(sweepEx, "Event corrections sweep failed at startup");
                        }
                    });
                }
            }
            else
            {
                // The factory may have resolved to the legacy path (lol_review.db); in that
                // case CreateFreshDatabaseIfMissing was a no-op and DatabasePath points at a
                // file that does exist. If neither exists here, creation failed — log it.
                migrateLogger.CreateLogger("Startup").LogWarning(
                    "Schema upgrade skipped: no DB at {Path} after first-run check.",
                    writeFactory.DatabasePath);
            }
        }
        catch (Exception ex)
        {
            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Startup")
                .LogError(ex, "First-run DB creation / additive schema upgrade failed at startup");
        }
    }

    public static void PublishWhenStarted(this WebApplication app, SidecarHostSession hostSession,
        string bearerToken, bool isolatedHostTest)
    {
        var backgroundWork = app.Services.GetRequiredService<SidecarBackgroundWork>();
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

                hostSession.Publish(port, bearerToken);
                programLogger.LogInformation("Revu sidecar listening on 127.0.0.1:{Port}", port);
            }
            catch (Exception ex)
            {
                programLogger.LogError(ex, "Failed to publish sidecar handshake file");
                lifetime.StopApplication();
            }

            if (isolatedHostTest) return;

            backgroundWork.TryRun("startup recording registration", () =>
                app.Services.GetRequiredService<RecordingRegistrationService>().ReconcileAsync());

            backgroundWork.TryRun("startup Ascent scan", () =>
            {
                var write = app.Services.GetRequiredService<WriteServices>();
                return AscentRecordingScan.RunWithRetryAsync(write.VodScan, write.Config, write.Vod,
                    backgroundWork, async () =>
                    {
                        await write.BackupGuard.EnsureBackedUpAsync();
                    }, gameId => app.Services.GetRequiredService<SidecarEventHub>().Publish("vodLinked", new { gameId }),
                    programLogger, registrations: app.Services.GetRequiredService<RecordingRegistrationService>());
            });

            // v3.10.1: confirm or fill the matchup of games that missed their post-game
            // pass — the app closed within five minutes of a game, or the game was played
            // on a build without the pass — so nobody has to press Backfill in Settings.
            // Bounded (newest ten still waiting), signed-in only, behind the same backup
            // guard and tracked background work. The
            // per-game pass (SidecarGameFlowCoordinator) covers the game just played.
            backgroundWork.TryRun("startup matchup heal", async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), backgroundWork.Stopping);
                    var write = app.Services.GetRequiredService<WriteServices>();
                    if (!write.Config.HasValidRiotSession) return;
                    var pending = await write.Games.GetGameIdsMissingEnemyLanerAsync();
                    if (pending.Count == 0) return;

                    await write.BackupGuard.EnsureBackedUpAsync();
                    var result = await write.EnemyLanerBackfill.RunAsync(maxGames: 10);
                    programLogger.LogInformation(
                        "Startup matchup heal: {Updated} confirmed, {Failed} not yet available, {Skipped} without positions ({Pending} waiting)",
                        result.Updated, result.Failed, result.Skipped, pending.Count);
                    if (result.Updated > 0)
                    {
                        app.Services.GetRequiredService<SidecarEventHub>()
                            .Publish("matchupUpdated", new { gameId = (long?)null, updated = result.Updated });
                    }
                }
                catch (Exception ex)
                {
                    programLogger.LogDebug(ex, "Startup matchup heal skipped (non-fatal)");
                }
            });
        });
    }

    private static int ExtractPort(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return 0;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri)) return uri.Port;
        return 0;
    }
}

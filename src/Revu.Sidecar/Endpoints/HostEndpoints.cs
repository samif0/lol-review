#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapHost(WebApplication app, JsonSerializerOptions jsonOptions, SidecarHostSession hostSession)
    {
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
                return Results.Json(new { status = "degraded" }, jsonOptions, statusCode: 503);
            }
        });

        // Launch/data-root compatibility is authenticated; bearer credentials stay in
        // the desktop main process and never enter a renderer.
        app.MapGet("/api/host", () => Results.Json(hostSession.Identity, jsonOptions));

        // Private host lifecycle operation; the renderer command map does not expose it.
        // Complete the response before Kestrel begins graceful shutdown.
        app.MapPost("/api/host/shutdown", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            context.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // LCU LIVE CHANNEL (Batch 5).
        // ─────────────────────────────────────────────────────────────────────────────

        // ── GET /api/events (token-gated): Server-Sent Events stream of LCU messages ──
        // The live wire to the webview. The SidecarGameFlowCoordinator publishes each LCU
        // message (champ-select start/update/cancel, game start/in-progress/end, missed
        // reviews, LCU connection) as a {type, payload} event onto SidecarEventHub; this
        // endpoint streams them to a single connected client as text/event-stream. Each
        // SSE record is `event: <type>\ndata: <json>\n\n`. The desktop main process opens this with
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
                    // Staged pre-game choices, so a webview reload mid-champ-select
                    // renders exactly what the EOG write will persist instead of
                    // silently desyncing to defaults.
                    preGameMood = live.PreGameMood,
                    intention = live.Intention,
                    intentionSource = live.IntentionSource,
                    intentCleared = live.IntentCleared,
                    practicedObjectiveIds = live.PracticedObjectiveIds,
                    // v3.7: the latest enforcement, so a reload mid-lockout keeps the banner.
                    hardStop = live.HardStop,
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
    }
}

#nullable enable

using System.Text.Json;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    // 4.1 full-game (VOD) sharing (contract 7.7). Token-gated like every /api route, and absent
    // from IsolatedHostPolicy, so an isolated host denies all of them. The recording is always
    // the one linked to the game; the renderer only ever sends a game id.
    private static void MapVodShare(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // GET /api/vod-share/status?gameId=N: recording, estimates, pool, limits, job, live share.
        app.MapGet("/api/vod-share/status", async (long gameId, VodShareWorker worker, CancellationToken ct) =>
        {
            if (gameId <= 0) return Results.Json(new { ok = false, error = "gameId required" }, jsonOptions, statusCode: 400);
            try
            {
                return Results.Json(await worker.GetStatusAsync(gameId, ct), jsonOptions);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
        });

        // POST /api/vod-share/start { gameId, quality?, includeNotes? }: checks, then queues the
        // job and returns at once. Refusals are 200 { ok: false, errorCode, error }.
        app.MapPost("/api/vod-share/start", async (VodShareStartBody body, VodShareWorker worker, CancellationToken ct) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.Json(new { ok = false, error = "gameId required" }, jsonOptions, statusCode: 400);
            try
            {
                var reply = await worker.StartAsync(body.GameId, body.Quality ?? "1080p", body.IncludeNotes ?? false, ct);
                return Results.Json(reply.Body, jsonOptions, statusCode: reply.Status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Json(new { ok = false, error = "The request was cancelled." }, jsonOptions, statusCode: 499);
            }
        });

        // POST /api/vod-share/cancel { gameId }: stops a running share (its remote copy is dropped).
        app.MapPost("/api/vod-share/cancel", (VodShareGameBody body, VodShareWorker worker) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.Json(new { ok = false, error = "gameId required" }, jsonOptions, statusCode: 400);
            return Results.Json(new { ok = true, cancelled = worker.Cancel(body.GameId) }, jsonOptions);
        });

        // POST /api/vod-share/stop { gameId }: cancels a running share, then deletes the live link
        // (now when signed in, else queued for the next signed-in start). Never bound to the
        // request: a renderer that stops waiting must not leave the link half removed.
        app.MapPost("/api/vod-share/stop", async (VodShareGameBody body, VodShareWorker worker, SidecarBackgroundWork work) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.Json(new { ok = false, error = "gameId required" }, jsonOptions, statusCode: 400);
            return Results.Json(await worker.StopAsync(body.GameId, work.Stopping), jsonOptions);
        });
    }
}

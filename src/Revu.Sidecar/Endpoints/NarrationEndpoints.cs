#nullable enable

using System.Text.Json;
using Revu.Core.Data;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    // 3.14 narrated clips (C6 6.2b, 6.4 to 6.7). Every route here is denied in isolated
    // host mode (IsolatedHostPolicy is default-deny and none of them is allowlisted).
    private static void MapNarration(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // GET /api/clip/share-status?bookmarkId=N: the last known background share state.
        app.MapGet("/api/clip/share-status", (long bookmarkId, ClipShareWorker share) =>
            Results.Json(share.GetStatus(bookmarkId), jsonOptions));

        // POST /api/clip/narration/save
        // Private desktop-main API: never add to the renderer command allowlist. Electron main
        // writes <NarrationDirectory>/<narrationId>.webm, then calls this; the sidecar derives
        // the path from the id itself and never accepts one.
        app.MapPost("/api/clip/narration/save", async (SaveNarrationBody body, HttpContext ctx, WriteServices w,
            ClipShareWorker share, NarrationTranscriptionWorker transcriber, RemoteClipCleanupStore cleanup,
            SidecarEventHub hub, SidecarBackgroundWork work, ILogger<Program> log) =>
        {
            var commands = NarrationCommandsFor(w, share, transcriber, cleanup, hub, work, log);
            return (await commands.SaveAsync(body, ctx.RequestAborted)).ToResult(jsonOptions);
        });

        // POST /api/clip/narration/mix  { gameId, bookmarkId, offsetMs, gameVolume, narrationVolume, duck }
        app.MapPost("/api/clip/narration/mix", async (MixNarrationBody body, HttpContext ctx, WriteServices w,
            ClipShareWorker share, NarrationTranscriptionWorker transcriber, RemoteClipCleanupStore cleanup,
            SidecarEventHub hub, SidecarBackgroundWork work, ILogger<Program> log) =>
        {
            var commands = NarrationCommandsFor(w, share, transcriber, cleanup, hub, work, log);
            return (await commands.MixAsync(body, ctx.RequestAborted)).ToResult(jsonOptions);
        });

        // POST /api/clip/narration/delete  { gameId, bookmarkId }  (idempotent)
        app.MapPost("/api/clip/narration/delete", async (NarrationTargetBody body, WriteServices w,
            ClipShareWorker share, NarrationTranscriptionWorker transcriber, RemoteClipCleanupStore cleanup,
            SidecarEventHub hub, SidecarBackgroundWork work, ILogger<Program> log) =>
        {
            var commands = NarrationCommandsFor(w, share, transcriber, cleanup, hub, work, log);
            return (await commands.DeleteAsync(body)).ToResult(jsonOptions);
        });

        // POST /api/clip/narration/transcribe  { gameId, bookmarkId }
        app.MapPost("/api/clip/narration/transcribe", async (NarrationTargetBody body, WriteServices w,
            ClipShareWorker share, NarrationTranscriptionWorker transcriber, RemoteClipCleanupStore cleanup,
            SidecarEventHub hub, SidecarBackgroundWork work, ILogger<Program> log) =>
        {
            var commands = NarrationCommandsFor(w, share, transcriber, cleanup, hub, work, log);
            return (await commands.TranscribeAsync(body)).ToResult(jsonOptions);
        });
    }

    private static NarrationCommands NarrationCommandsFor(WriteServices w, ClipShareWorker share,
        NarrationTranscriptionWorker transcriber, RemoteClipCleanupStore cleanup, SidecarEventHub hub,
        SidecarBackgroundWork work, ILogger log) =>
        new(w.Vod, w.ClipNarrations, w.Config, w.NarrationMixer, w.ClipUpload, w.BackupGuard.EnsureBackedUpAsync,
            share, transcriber, cleanup, hub, work.Stopping, AppDataPaths.NarrationDirectory, log);
}

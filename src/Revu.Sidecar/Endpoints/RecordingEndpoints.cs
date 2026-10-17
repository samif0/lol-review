using System.Text.Json;
using Revu.Core.Lcu;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapRecording(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // Private desktop-main API: bearer credentials are held only by the host;
        // these routes must never be added to the renderer command allowlist.
        app.MapGet("/api/recording/context", (GameMonitorService monitor) =>
            Results.Json(monitor.ObserveRecordingContext(), jsonOptions));

        app.MapGet("/api/recording/session/{sessionId:guid}", async (Guid sessionId, RecordingRegistrationService registrations) =>
        {
            var status = await registrations.GetAsync(sessionId);
            return status is null ? Results.NotFound(new { ok = false, error = "Unknown recording session." })
                : Results.Json(status, jsonOptions);
        });

        app.MapPost("/api/recording/register", (RecordingRegistration body,
            RecordingRegistrationService registrations, SidecarBackgroundWork work) =>
            RunRecordingWork(work, "recording registration",
                async () => Results.Json(await registrations.RegisterAsync(body), jsonOptions)));

        // Host-only, like register: the host rewrote a linked recording at the same path
        // (identical packets, indexed layout) and reports only its new size.
        app.MapPost("/api/recording/relayout", (RecordingRelayout body,
            RecordingRegistrationService registrations, SidecarBackgroundWork work) =>
            RunRecordingWork(work, "recording relayout", async () =>
            {
                var result = await registrations.UpdateFileSizeAsync(body.SessionId, body.FileSize);
                return result is null ? Results.NotFound(new { ok = false, error = "Unknown recording session." })
                    : Results.Json(result, jsonOptions);
            }));

        // Host-only: called after a match's recording settles. Deletes the oldest finished
        // recordings until the folder fits the user's limit (no-op without one or mid-game).
        app.MapPost("/api/recording/prune", (RecordingStoragePruner pruner, SidecarBackgroundWork work) =>
            RunRecordingWork(work, "recording storage prune",
                async () => Results.Json(await pruner.PruneAsync(work.Stopping), jsonOptions)));
    }

    // Accepted work drains during shutdown even if the host HTTP request disconnects.
    private static async Task<IResult> RunRecordingWork(SidecarBackgroundWork work, string name, Func<Task<IResult>> operation)
    {
        var response = new TaskCompletionSource<IResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!work.TryRun(name, async () =>
        {
            try { response.SetResult(await operation()); }
            catch (RecordingConflictException ex)
            { response.SetResult(Results.Conflict(new { ok = false, error = ex.Message })); }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
            { response.SetResult(Results.BadRequest(new { ok = false, error = ex.Message })); }
            catch (Exception ex) { response.SetException(ex); }
        })) return Results.Json(new { ok = false, error = "Revu is shutting down." }, statusCode: 503);
        return await response.Task;
    }
}

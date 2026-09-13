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

        app.MapPost("/api/recording/register", async (RecordingRegistration body,
            RecordingRegistrationService registrations, SidecarBackgroundWork work) =>
        {
            // Accepted work drains during shutdown even if the host HTTP request disconnects.
            var response = new TaskCompletionSource<IResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!work.TryRun("recording registration", async () =>
            {
                try
                {
                    var result = await registrations.RegisterAsync(body);
                    response.SetResult(Results.Json(result, jsonOptions));
                }
                catch (RecordingConflictException ex)
                { response.SetResult(Results.Conflict(new { ok = false, error = ex.Message })); }
                catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
                { response.SetResult(Results.BadRequest(new { ok = false, error = ex.Message })); }
                catch (Exception ex) { response.SetException(ex); }
            })) return Results.Json(new { ok = false, error = "Revu is shutting down." }, statusCode: 503);
            return await response.Task;
        });
    }
}

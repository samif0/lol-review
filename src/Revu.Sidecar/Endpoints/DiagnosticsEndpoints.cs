#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    /// <summary>
    /// Opt-in switch for the practice-capture research routes (see PRACTICE-CAPTURE.md).
    /// They are mapped only when the sidecar starts with this set to "1", so shipped
    /// builds never expose them.
    /// </summary>
    public const string PracticeDiagnosticsSetting = "REVU_PRACTICE_DIAGNOSTICS";

    private static void MapDiagnostics(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // Practice capture (scripts/Practice-Capture.ps1): a diagnostic instrument with
        // no app caller. Read through configuration, which includes the environment.
        if (app.Configuration[PracticeDiagnosticsSetting] != "1") return;

        app.MapPost("/api/diagnostics/practice/start", (PracticeCaptureService capture) => Results.Json(capture.Arm(), jsonOptions));

        app.MapGet("/api/diagnostics/practice/status", (PracticeCaptureService capture) => Results.Json(capture.Status, jsonOptions));

        app.MapPost("/api/diagnostics/practice/stop", async (PracticeCaptureService capture) => Results.Json(await capture.StopAsync(), jsonOptions));
    }
}

#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapDiagnostics(WebApplication app, JsonSerializerOptions jsonOptions)
    {
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
        app.MapPost("/api/diagnostics/practice/start", (PracticeCaptureService capture) => Results.Json(capture.Arm(), jsonOptions));

        app.MapGet("/api/diagnostics/practice/status", (PracticeCaptureService capture) => Results.Json(capture.Status, jsonOptions));

        app.MapPost("/api/diagnostics/practice/stop", async (PracticeCaptureService capture) => Results.Json(await capture.StopAsync(), jsonOptions));
    }
}

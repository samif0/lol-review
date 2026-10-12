#nullable enable

using System.Text.Json;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapMissedGames(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/missed-games (token-gated) ────────────────────────────────────────
        // Finished ranked games the League client's match history holds but Revu never
        // saved (the app was closed, or an earlier capture was missed), newest first. The
        // monitor's startup and post-game reconcile passes fill this in memory; the game
        // it tracked itself is saved automatically and never waits here.
        app.MapGet("/api/missed-games", (SidecarGameFlowCoordinator flow) =>
            Results.Json(new { games = flow.MissedGames() }, jsonOptions));

        // ── POST /api/missed-games/resolve { gameId, action: "save" | "dismiss" } ─────
        // save: the same persistence path as a normal game end (first-write safety backup,
        // game row, session log, live timeline when the monitor kept one, post-game passes).
        // dismiss: a durable dismissal (missed_game_decisions); the game is never offered again.
        app.MapPost("/api/missed-games/resolve", async (MissedGameResolveBody body, SidecarGameFlowCoordinator flow) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            var action = (body.Action ?? "").Trim().ToLowerInvariant();
            if (action is not ("save" or "dismiss"))
                return Results.BadRequest(new { error = "action must be save or dismiss" });

            var outcome = action == "save"
                ? await flow.SaveMissedGameAsync(body.GameId)
                : await flow.DismissMissedGameAsync(body.GameId);
            return outcome switch
            {
                MissedGameOutcome.NotFound => Results.NotFound(new { error = "That game is no longer waiting to be saved." }),
                MissedGameOutcome.Failed => Results.Json(
                    new { ok = false, error = "Couldn't save this game. Try again." }, jsonOptions, statusCode: 500),
                _ => Results.Json(new
                {
                    ok = true,
                    gameId = body.GameId,
                    saved = outcome == MissedGameOutcome.Saved,
                    dismissed = outcome is MissedGameOutcome.Dismissed or MissedGameOutcome.Declined,
                }, jsonOptions),
            };
        });
    }
}

// Action is "save" or "dismiss".
internal sealed record MissedGameResolveBody(long GameId, string? Action);

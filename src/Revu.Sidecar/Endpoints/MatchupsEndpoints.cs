#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapMatchups(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ─────────────────────────────────────────────────────────────────────────────
        // v3.9 Matchup journal. GET /api/matchups is the read half (cards grouped by
        // lane → matchup key, newest first, plus the "New card from last game" preview);
        // GET /api/matchups/export renders the Markdown the page copies to the
        // clipboard. The POST /api/matchup/* writes reuse MatchupsRepository's
        // validation verbatim — its ArgumentException IS the user-facing sentence, so
        // it surfaces as a 400 {error}. The page refetches GET /api/matchups after
        // every write except the inline note autosave.
        // ─────────────────────────────────────────────────────────────────────────────

        app.MapGet("/api/matchups", async (MatchupsSnapshotBuilder b, CancellationToken ct) =>
            Results.Json(await b.BuildAsync(ct), jsonOptions));

        // GET /api/matchups/export?lane=<lane>&last=<n> — both optional: lane keeps one
        // lane, last keeps the N newest cards (after the lane filter). Returns the
        // Markdown (one H2 per lane, one H3 per matchup, Prior / Observed per card with
        // its date) plus the card count it covers; the page copies it to the clipboard.
        app.MapGet("/api/matchups/export", async (string? lane, long? last, MatchupsSnapshotBuilder b, ILogger<Program> log) =>
        {
            if (!string.IsNullOrWhiteSpace(lane) && !MatchupLanes.IsValid(lane))
                return Results.BadRequest(new { error = "Pick a lane: top, jungle, mid, bot or support." });
            var (markdown, count) = await b.BuildExportAsync(lane, last);
            var fileName = $"revu-matchups-{DateTime.Now:yyyyMMdd-HHmm}.md";
            log.LogInformation("Matchup journal export built ({Count} cards, {Chars} chars)", count, markdown.Length);
            return Results.Json(new { ok = true, markdown, count, fileName }, jsonOptions);
        });

        // POST /api/matchup/create  { lane, allyChamps[], enemyChamps[], prior?, observed?, gameId? }
        app.MapPost("/api/matchup/create", async (CreateMatchupBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null) return Results.BadRequest(new { error = "body required" });
            // games.game_id is a real FOREIGN KEY target: a link to a game that isn't on
            // record would fail inside SQLite as an opaque 500, so say it plainly first.
            if (body.GameId is > 0 && await w.Games.GetAsync(body.GameId.Value) is null)
                return Results.BadRequest(new { error = "That game is not on record." });
            await w.BackupGuard.EnsureBackedUpAsync();
            try
            {
                var id = await w.Matchups.CreateAsync(
                    body.Lane, body.AllyChamps, body.EnemyChamps, body.Prior, body.Observed, body.GameId);
                log.LogInformation("Matchup card created: {Id} ({Lane})", id, body.Lane);
                return Results.Json(new { ok = true, id }, jsonOptions);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // POST /api/matchup/from-last-game  {} — pre-fill lane + champions from the most
        // recent game's participants (the newest ranked / manual game — the same scope
        // every games list uses), prior / observed left empty for the player to
        // write. Idempotent per game: when a card already links to that game its id
        // comes back with created=false instead of a duplicate. 422 when there is no
        // game, or its lane / champions can't be resolved (same sentences the read
        // snapshot shows on the disabled button).
        // v3.9.2: a game recovered from the client's match history can lack the
        // opponents (no per-player positions in that payload). MatchupFromLastGame
        // first resolves such a game from Match-V5 when the player is signed in (a
        // bounded single-game lookup) and re-reads the row; then, if both sides are
        // known and the lane came from the game itself, the card is created —
        // otherwise the route answers 200 { partial: true } with the lane, the
        // player's side BY SLOT and the game link, and the page opens the form for
        // the player to finish instead of creating a half-empty card.
        app.MapPost("/api/matchup/from-last-game", async (WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            var last = await MatchupsSnapshotBuilder.ResolveLastGameAsync(w.Games, w.Matchups, w.Config.PrimaryRole);
            if (last.Game is null)
                return Results.Json(new { ok = false, error = MatchupsSnapshotBuilder.NoGamesReason }, jsonOptions, statusCode: 422);
            if (last.Existing is not null)
                return Results.Json(new { ok = true, id = last.Existing.Id, created = false }, jsonOptions);
            if (last.Prefill is null)
                return Results.Json(new { ok = false, error = MatchupsSnapshotBuilder.NoPrefillReason }, jsonOptions, statusCode: 422);

            await w.BackupGuard.EnsureBackedUpAsync();
            var (game, prefill) = await MatchupFromLastGame.HealAsync(
                last.Game, last.Prefill, w.Config, w.EnemyLanerBackfill, w.Games, log, ct: ct);

            if (!MatchupFromLastGame.ShouldCreateOutright(game, prefill))
            {
                return Results.Json(new
                {
                    ok = true,
                    created = false,
                    partial = true,
                    gameId = game.GameId,
                    gameLabel = MatchupsSnapshotBuilder.GameLabel(game),
                    lane = prefill.Lane,
                    laneIsGuess = prefill.LaneIsGuess,
                    // v3.10.1: the matchup is a game-end estimate Riot has not confirmed yet.
                    estimated = Revu.Core.Models.MatchupSources.NeedsConfirmation(game.MatchupSource),
                    // By form slot ("" = unknown), so a lone support lands in the support field.
                    allyChamps = prefill.AllySlots,
                    enemyChamps = prefill.EnemySlots,
                }, jsonOptions);
            }

            try
            {
                var id = await w.Matchups.CreateAsync(
                    prefill.Lane, prefill.AllyChamps, prefill.EnemyChamps, gameId: game.GameId);
                log.LogInformation("Matchup card {Id} pre-filled from game {GameId} ({Title})", id, game.GameId, prefill.Title);
                return Results.Json(new { ok = true, id, created = true }, jsonOptions);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // POST /api/matchup/update  { id, lane, allyChamps[], enemyChamps[], prior?, observed? }
        // Full edit; the game link is not part of it.
        app.MapPost("/api/matchup/update", async (UpdateMatchupBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            try
            {
                var found = await w.Matchups.UpdateAsync(
                    body.Id, body.Lane, body.AllyChamps, body.EnemyChamps, body.Prior, body.Observed);
                if (!found) return Results.NotFound(new { error = "That matchup card no longer exists." });
                log.LogInformation("Matchup card updated: {Id}", body.Id);
                return Results.Json(new { ok = true }, jsonOptions);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // POST /api/matchup/notes  { id, prior?, observed? } — the inline autosave. A
        // null / absent field is left unchanged, so the page sends only what changed.
        app.MapPost("/api/matchup/notes", async (MatchupNotesBody body, WriteServices w) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            try
            {
                var found = await w.Matchups.UpdateNotesAsync(body.Id, body.Prior, body.Observed);
                if (!found) return Results.NotFound(new { error = "That matchup card no longer exists." });
                return Results.Json(new { ok = true }, jsonOptions);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // POST /api/matchup/delete  { id } — HARD delete (DELETE FROM matchups). The
        // page confirms before calling; the first-write safety backup is the net here.
        app.MapPost("/api/matchup/delete", async (MatchupIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Matchups.DeleteAsync(body.Id);
            log.LogInformation("Matchup card deleted: {Id}", body.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });
    }
}

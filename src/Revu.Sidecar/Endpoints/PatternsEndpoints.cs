#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapPatterns(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/patterns (token-gated): read-only cross-game pattern cards ───────
        // Pattern cards + their ordered moment playlists. Mark-reviewed (and the
        // per-moment note/clip writes) are WRITE ops and are DEFERRED — the DTO carries
        // isReviewed + a carryForwardNote placeholder the frontend renders read-only.
        app.MapGet("/api/patterns", async (PatternsSnapshotBuilder b, CancellationToken ct) =>
            Results.Json(await b.BuildAsync(ct), jsonOptions));

        // ─────────────────────────────────────────────────────────────────────────────
        // PATTERN REVIEW writes (Batch 3). Close out a cross-game pattern + per-moment
        // note autosave (which silently clips the moment's window the first time). Both
        // reuse IEvidenceRepository methods verbatim. Token-gated + backup-guarded.
        // ─────────────────────────────────────────────────────────────────────────────

        // POST /api/pattern/mark-reviewed  { patternKey, kind?, momentCount? }
        // Mirrors PatternReviewViewModel.MarkReviewedAsync → IEvidenceRepository.
        // MarkPatternReviewedAsync(patternKey, kind, momentCount). kind + momentCount ride
        // in the body (the frontend has both from the loaded /api/patterns snapshot); when
        // kind is absent it's resolved from the live pattern cards so an older frontend
        // still closes the pattern out correctly.
        app.MapPost("/api/pattern/mark-reviewed", async (MarkPatternReviewedBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.PatternKey))
                return Results.BadRequest(new { error = "patternKey required" });
            await w.BackupGuard.EnsureBackedUpAsync();

            var key = body.PatternKey.Trim();
            var kind = (body.Kind ?? "").Trim();
            var momentCount = body.MomentCount ?? 0;

            // Resolve kind/momentCount from the live cards if the frontend didn't send
            // them (full candidate set — the pattern may sit beyond the display cap).
            if (kind.Length == 0 || body.MomentCount is null)
            {
                var card = (await w.Evidence.GetPatternCardsAsync(
                    limit: Revu.Core.Constants.PatternConstants.PatternCandidateLimit))
                    .FirstOrDefault(c => c.PatternKey == key);
                if (card is not null)
                {
                    if (kind.Length == 0) kind = card.Kind;
                    if (body.MomentCount is null)
                    {
                        var moments = await w.Evidence.GetPatternMomentsAsync(card);
                        momentCount = moments.Count;
                    }
                }
            }

            await w.Evidence.MarkPatternReviewedAsync(key, kind, momentCount);
            log.LogInformation("Pattern reviewed: {Key} ({Kind}, {Count} moments)", key, kind, momentCount);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/pattern/moment/note  { evidenceId, text, gameId?, championName?, vodPath?,
        //   polarity?, startTimeS?, endTimeS? }
        // Mirrors PatternReviewViewModel.FlushNoteAsync: always persist the note via
        // UpdateNoteAsync; then — only when the note is non-empty AND the moment has a VOD
        // AND no clip yet — silently extract a padded clip over the moment's window,
        // add a clip bookmark, and PROMOTE this evidence row to be that clip
        // (AttachClipToEvidenceAsync), so it shows as a saved clip rather than a duplicate.
        // The padded window mirrors ClipWindowFor (lead 8s / trail 4s for single points).
        // alreadyClipped lets the frontend suppress re-extraction (mirrors HasClip gate).
        app.MapPost("/api/pattern/moment/note", async (PatternMomentNoteBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || body.EvidenceId <= 0)
                return Results.BadRequest(new { error = "evidenceId required" });
            await w.BackupGuard.EnsureBackedUpAsync();

            var text = (body.Text ?? "").Trim();
            await w.Evidence.UpdateNoteAsync(body.EvidenceId, text);

            var clipped = false;
            string? clipPath = null;

            // Clip the moment's window once, only when there's a note + a playable VOD and
            // it hasn't been clipped yet (mirror the VM's clipNeeded guard). A start-less
            // moment (game-level anchor: recurring tag, rule break) saves its note but
            // never extracts a clip — there is no in-game second to clip around.
            var hasVod = !string.IsNullOrWhiteSpace(body.VodPath);
            if (text.Length > 0 && hasVod && !body.AlreadyClipped && body.GameId is > 0 && body.StartTimeS is not null)
            {
                try
                {
                    var start = Math.Max(0, body.StartTimeS ?? 0);
                    var end = body.EndTimeS ?? 0;
                    // Padded window: real range when end>start, else lead/trail around point.
                    var (startS, endS) = end > start
                        ? (start, end)
                        : (Math.Max(0, start - PatternClipLeadSeconds), start + PatternClipTrailSeconds);

                    var note = text.Length > 0 ? text : (body.Title ?? "Clip");
                    var quality = Revu.Core.Data.Repositories.EvidencePolarities.Normalize(body.Polarity);
                    clipPath = await w.Clips.ExtractClipAsync(
                        body.VodPath!, startS, endS, body.ChampionName ?? "", w.Config.ClipsFolder);

                    if (!string.IsNullOrEmpty(clipPath))
                    {
                        var bookmarkId = await w.Vod.AddBookmarkAsync(
                            gameId: body.GameId.Value,
                            gameTimeSeconds: startS,
                            note: note,
                            clipStartSeconds: startS,
                            clipEndSeconds: endS,
                            clipPath: clipPath,
                            quality: quality);
                        // Promote this moment's own evidence row to BE the clip.
                        await w.Evidence.AttachClipToEvidenceAsync(body.EvidenceId, bookmarkId, startS, endS);
                        clipped = true;
                        log.LogInformation("Pattern moment {EvId} clipped: {StartS}-{EndS}s -> {Path}", body.EvidenceId, startS, endS, clipPath);
                    }
                }
                catch (Exception ex)
                {
                    // Note already saved — clip failure is non-fatal (mirror VM's try/catch).
                    log.LogWarning(ex, "Pattern moment {EvId} note saved but clip failed", body.EvidenceId);
                }
            }

            return Results.Json(new { ok = true, clipped, clipPath }, jsonOptions);
        });
    }

    private const int PatternClipLeadSeconds = 8;
    private const int PatternClipTrailSeconds = 4;
}

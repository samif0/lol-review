#nullable enable

using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

/// <summary>
/// The shared "persist an already-extracted clip" tail used by BOTH the manual VOD
/// clip tool (POST /api/clip/extract) and the on-demand auto-clipper. After ffmpeg
/// has written the .mp4, this records the bookmark, marks the tagged objective
/// practiced, and upserts the evidence row — the one sequence so the two callers
/// can never diverge.
/// </summary>
public static class ClipPersistence
{
    /// <summary>
    /// Record a clip's bookmark + evidence + objective-practiced side effects.
    /// </summary>
    /// <param name="sourceKey">
    /// Stable evidence dedupe key. Pass null for the manual path to use the default
    /// <c>clip:{bookmarkId}</c> (computed after insert); the auto-clipper passes
    /// <c>autoclip:{gameId}:{eventId}</c> so re-runs are idempotent.
    /// </param>
    /// <returns>The created bookmark id.</returns>
    public static async Task<long> PersistAsync(
        IVodRepository vod,
        IObjectivesRepository objectives,
        IEvidenceRepository evidence,
        long gameId,
        int startS,
        int endS,
        string clipPath,
        string note,
        string quality,
        long? objectiveId,
        long? promptId = null,
        string? sourceKey = null)
    {
        var bookmarkId = await vod.AddBookmarkAsync(
            gameId: gameId,
            gameTimeSeconds: startS,
            note: note,
            clipStartSeconds: startS,
            clipEndSeconds: endS,
            clipPath: clipPath,
            objectiveId: objectiveId,
            quality: quality,
            promptId: promptId);

        // Tagging the clip to an objective marks it practiced for this game (preserve
        // any existing execution note — only flip practiced->true, never clobber).
        if (objectiveId is long oid)
        {
            var existing = await objectives.GetGameObjectivesAsync(gameId);
            var exNote = existing.FirstOrDefault(r => r.ObjectiveId == oid)?.ExecutionNote ?? "";
            await objectives.RecordGameAsync(gameId, oid, practiced: true, executionNote: exNote);
        }

        // Evidence row: polarity = quality or neutral; a quality-tagged clip is already
        // a judgement (status=evidence), an untagged one needs review.
        var q = (quality ?? "").Trim();
        await evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: bookmarkId,
            SourceKey: sourceKey ?? $"clip:{bookmarkId}",
            StartTimeSeconds: startS,
            EndTimeSeconds: endS,
            Title: note,
            Note: note,
            ObjectiveId: objectiveId,
            Polarity: string.IsNullOrWhiteSpace(q)
                ? EvidencePolarities.Neutral
                : EvidencePolarities.Normalize(q),
            Status: string.IsNullOrWhiteSpace(q)
                ? EvidenceStatuses.NeedsReview
                : EvidenceStatuses.Evidence));

        return bookmarkId;
    }
}

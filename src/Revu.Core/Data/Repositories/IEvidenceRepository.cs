#nullable enable

namespace Revu.Core.Data.Repositories;

public interface IEvidenceRepository
{
    Task<long> UpsertAsync(EvidenceUpsert item);

    Task<IReadOnlyList<EvidenceItemRecord>> GetForGameAsync(long gameId, bool includeDismissed = false);

    Task<IReadOnlyList<EvidenceItemRecord>> GetForObjectiveAsync(long objectiveId, bool includeDismissed = false);

    Task<IReadOnlyList<EvidenceItemRecord>> GetRecentAsync(int limit = 20, bool includeDismissed = false);

    Task<int> CountPendingAsync();

    Task UpdateStatusAsync(long evidenceId, string status);

    /// <summary>Permanently remove an evidence row by id (hard delete).</summary>
    Task DeleteAsync(long evidenceId);

    Task UpdatePolarityAsync(long evidenceId, string polarity);

    Task UpdateObjectiveAsync(long evidenceId, long? objectiveId);

    /// <summary>
    /// P-027: tag (or untag, when promptId is null) an evidence row to the custom
    /// prompt it answers, so the post-game review can group clips under the prompt.
    /// Independent of objective_id (both coexist); carries no score award.
    /// </summary>
    Task UpdatePromptAsync(long evidenceId, long? promptId);

    Task UpdateNoteAsync(long evidenceId, string note);

    /// <summary>
    /// Promote an existing evidence row to the saved clip backed by a bookmark
    /// (sets source_kind=clip + source_id, and unhides it from needs-review).
    /// Used by the Pattern Review auto-clip so the moment becomes a real clip
    /// without creating a duplicate evidence row.
    /// </summary>
    Task AttachClipToEvidenceAsync(long evidenceId, long bookmarkId, int clipStartS, int clipEndS);

    Task<IReadOnlyList<ObjectivePatternCard>> GetPatternCardsAsync(int limit = 6);

    /// <summary>
    /// Resolve the ordered (oldest-first) moments that compose a pattern, joined
    /// to each game's champion/result and matched VOD path. Drives the Pattern
    /// Review viewer's cross-game moment playlist.
    /// </summary>
    Task<IReadOnlyList<PatternMoment>> GetPatternMomentsAsync(ObjectivePatternCard pattern);

    /// <summary>Mark a pattern reviewed (upsert by pattern key); ticks the dashboard stat.</summary>
    Task MarkPatternReviewedAsync(string patternKey, string kind, int momentCount);

    /// <summary>Count of distinct patterns the user has marked reviewed.</summary>
    Task<int> CountReviewedPatternsAsync();

    /// <summary>
    /// pattern_key → reviewed_at (unix seconds) for every reviewed pattern.
    /// reviewed_at is the re-arm watermark: PatternReviewGate treats a pattern
    /// with enough moments NEWER than its stamp as pending again.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> GetReviewedPatternsAsync();

    // ── Pattern-evidence materializer support ───────────────────────────────

    /// <summary>A death-audit moment that was promoted to a clip (source key
    /// rewritten; title and exact window preserved) for the death at this
    /// second; null when none. Lets re-classify/clear retitle in place.</summary>
    Task<long?> FindPromotedDeathAuditAsync(long gameId, int gameTimeSeconds);

    /// <summary>A clip-promoted twin of a materialized moment (same title +
    /// exact original window); null when none. Guards re-materialization from
    /// duplicating promoted region/gank moments.</summary>
    Task<long?> FindPromotedTwinAsync(long gameId, string title, int startTimeSeconds, int endTimeSeconds);

    /// <summary>Rewrite an evidence row's title (detection keys off title).</summary>
    Task UpdateTitleAsync(long evidenceId, string title);

    /// <summary>Delete by dedupe identity; returns rows affected (0 when the
    /// row was promoted/rekeyed — the user's clip survives).</summary>
    Task<int> DeleteBySourceKeyAsync(long gameId, string sourceKind, string sourceKey);
}

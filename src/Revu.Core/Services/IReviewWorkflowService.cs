#nullable enable

namespace Revu.Core.Services;

public interface IReviewWorkflowService
{
    Task<ReviewSaveResult> SaveAsync(SaveReviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a saved review: clear the four queue-gating signals (games review
    /// columns, the session_log review markers, concept tags) plus the matchup note
    /// and any draft, returning the game to the UNREVIEWED queue. Deliberately
    /// PRESERVES objective progress (game_objectives + objectives.score/game_count)
    /// and the session_log behavioral fields (mental_rating / focus_adherence /
    /// rule_broken) so streaks + earned progress are untouched. The game row itself
    /// is kept (this is not a game delete).
    /// </summary>
    Task<ReviewSaveResult> DeleteAsync(long gameId, CancellationToken cancellationToken = default);

    Task<bool> SaveDraftAsync(ReviewDraftRequest request, CancellationToken cancellationToken = default);
}

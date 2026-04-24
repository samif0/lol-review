#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>
/// Free-form user-designed prompts attached to an objective + per-game answers.
///
/// Prompts have a phase ('pregame' | 'ingame' | 'postgame'). Rendered in champ
/// select (pregame) or post-game review (ingame + postgame) for every active
/// objective where the matching <c>practice_&lt;phase&gt;</c> bool is set.
///
/// Answers are scoped to a specific game, keyed by (prompt_id, game_id).
/// Re-saving for the same pair upserts.
/// </summary>
public interface IPromptsRepository
{
    // ── Prompt CRUD ─────────────────────────────────────────────────

    Task<long> CreatePromptAsync(long objectiveId, string phase, string label, int sortOrder);

    Task UpdatePromptAsync(long promptId, string phase, string label, int sortOrder);

    Task DeletePromptAsync(long promptId);

    Task<IReadOnlyList<ObjectivePrompt>> GetPromptsForObjectiveAsync(long objectiveId);

    // ── Rendered views for pre/post-game UI ─────────────────────────

    /// <summary>
    /// Return prompts that should render for the given phase right now. Joined
    /// with <c>objectives</c>; only returns rows where the parent objective is
    /// active AND the matching <c>practice_&lt;phase&gt;</c> bool is 1.
    /// Priority objectives sort first.
    ///
    /// When <paramref name="championName"/> is non-null, applies champion gating:
    /// objectives with no champion rows pass; objectives with champion rows only
    /// pass if one matches. NULL disables the filter entirely.
    /// </summary>
    Task<IReadOnlyList<ActivePrompt>> GetActivePromptsForPhaseAsync(string phase, string? championName = null);

    // ── Answers ─────────────────────────────────────────────────────

    /// <summary>
    /// Upsert an answer for (prompt_id, game_id). Re-saving overwrites the text
    /// and bumps updated_at. No answer row is created for empty text — if the
    /// user clears the field, the row is deleted so GetAnswersForGameAsync
    /// returns a clean slate.
    /// </summary>
    Task SaveAnswerAsync(long promptId, long gameId, string answerText);

    Task<IReadOnlyList<PromptAnswer>> GetAnswersForGameAsync(long gameId);

    // ── Pre-game draft answers (staged before the game row exists) ──

    /// <summary>
    /// Champ Select opens before the game exists in <c>games</c>. Answers get
    /// staged here keyed on the LCU session id, then copied into
    /// <c>prompt_answers</c> post-game via <see cref="PromotePreGameDraftsAsync"/>.
    /// </summary>
    Task SaveDraftAnswerAsync(string sessionKey, long promptId, string answerText);

    Task<IReadOnlyList<(long PromptId, string AnswerText)>> GetDraftAnswersAsync(string sessionKey);

    /// <summary>
    /// Copy all draft answers for a session into <c>prompt_answers</c> scoped to
    /// the given game_id, then clear the drafts. Idempotent — calling twice for
    /// the same session/game pair upserts (no duplicates).
    /// </summary>
    Task PromotePreGameDraftsAsync(string sessionKey, long gameId);
}

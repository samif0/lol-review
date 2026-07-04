#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/objective/notes?id=N (ObjectiveNotesPage).
//
// A read-only aggregation of EVERYTHING captured against ONE objective:
//   (1) per-game review notes, (2) per-game execution notes (the note typed
//   when recording the objective was practiced), (3) clips/bookmarks whose
//   objective_id == this objective. Each row jumps back to its source (review
//   page / VOD player) via plain frontend navigation — this endpoint never
//   writes, opens no dialogs, creates no clips.
//
// No SolidColorBrush — colors are plain *Hex strings. Property names PascalCase;
// the serializer is JsonNamingPolicy.CamelCase → camelCase wire shape (see
// desktop/ui/sample-objective-notes.json). Collections are never null.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level snapshot returned by GET /api/objective/notes?id=N.</summary>
public sealed record ObjectiveNotesDto(
    string GeneratedAt,
    long ObjectiveId,
    string ObjectiveTitle,
    // "Active • In-Game" / "Completed • Pre-Game" composite.
    string ObjectiveStatus,
    bool HasReviewNotes,
    bool HasExecutionNotes,
    bool HasBookmarks,
    bool HasPromptAnswers,
    // any of the four — drives the content-vs-empty switch.
    bool HasAnything,
    IReadOnlyList<ObjectiveReviewNoteRowDto> ReviewNotes,
    IReadOnlyList<ObjectiveExecutionNoteRowDto> ExecutionNotes,
    IReadOnlyList<ObjectiveBookmarkRowDto> Bookmarks,
    // custom-prompt answers grouped by prompt label/phase.
    IReadOnlyList<ObjectivePromptGroupDto> PromptAnswers);

/// <summary>A per-game review note. Mirrors ObjectiveReviewNoteRow.</summary>
public sealed record ObjectiveReviewNoteRowDto(
    long GameId,
    // "W • Aatrox • Jun 3, 2026".
    string Header,
    string Notes);

/// <summary>A per-game execution note. Mirrors ObjectiveExecutionNoteRow.</summary>
public sealed record ObjectiveExecutionNoteRowDto(
    long GameId,
    string Header,
    string Note);

/// <summary>A clip/bookmark tagged to the objective. Mirrors ObjectiveBookmarkRow.</summary>
public sealed record ObjectiveBookmarkRowDto(
    long BookmarkId,
    long GameId,
    int GameTimeSeconds,
    // "12:34" — minutes unpadded, seconds 2-digit, no hour rollover.
    string TimeLabel,
    // game header from the objective's game list, else "Game #N" fallback.
    string GameLabel,
    string Note,
    bool HasNote,
    // tags joined ", " (empty on parse failure / no tags).
    string Tags,
    bool HasTags,
    // ClipPath non-empty. Latent/dead field at parity — no UI effect. Preserved.
    bool HasClip);

/// <summary>
/// All answers the user typed under ONE custom prompt, grouped under its label +
/// phase. Each child row is a single game's answer (keyed (prompt_id, game_id)).
/// </summary>
public sealed record ObjectivePromptGroupDto(
    long PromptId,
    // the prompt the user designed, e.g. "What was my wave plan?".
    string Label,
    // "Pre-Game" / "In-Game" / "Post-Game" display label.
    string Phase,
    IReadOnlyList<ObjectivePromptAnswerRowDto> Answers);

/// <summary>One game's answer to a prompt. Jumps back to that game's review.</summary>
public sealed record ObjectivePromptAnswerRowDto(
    long GameId,
    // "W • Aatrox • Jun 3, 2026".
    string Header,
    string Answer);

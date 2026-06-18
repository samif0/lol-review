#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/review.
//
// Mirrors the WinUI ReviewViewModel's display shape for ONE game WITHOUT any
// XAML/WinUI types — no SolidColorBrush, only *Hex strings the Tauri frontend
// resolves itself. Property names are PascalCase here; the serializer is
// configured with JsonNamingPolicy.CamelCase in Program.cs, so the wire shape
// is camelCase and matches desktop/ui/sample-review.json verbatim.
//
// READ-ONLY: the review FORM (mental rating / debrief textareas / tags / save /
// skip) is a MUTATION surface and is therefore DEFERRED. The form's current
// saved values are shipped for read-only DISPLAY (see ReviewFormDto), with
// Editable=false and an EditingNote so the frontend can render the inputs
// disabled. Wiring save is out of scope for the read-only snapshot.
//
// The death-audit / evidence-triage / matchup-history / concept-tag-catalog
// sections added in Batch-1A are also READ-ONLY: classification, evidence
// triage, and tag selection are MUTATIONS deferred to a later write batch. The
// snapshot ships their CURRENT state (saved death classes, attached vs
// unassigned evidence, past matchup notes, the full tag catalog w/ selection
// flags) so the frontend can render the rows without yet writing anything.
//
// Null vs empty:
//   - subject is nullable: null = the DB has no reviewable game to sample
//     (fresh install / all games reviewed and none recent). The frontend shows
//     an empty state.
//   - Collections are always present (possibly empty), never null.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level review snapshot returned by GET /api/review.</summary>
public sealed record ReviewDto(
    string GeneratedAt,
    // null when there is no game to review (empty state).
    ReviewSubjectDto? Subject,
    // Read-only note explaining how the sample game was chosen (most-recent
    // unreviewed, else most-recent reviewed). Empty when Subject is null.
    string SubjectSourceText);

/// <summary>
/// The single game under review: the GameDisplayItem-style header, the 8-up
/// stat strip + laning line, the objective-practice context, the read-only
/// review form, plus the death audit, evidence triage, matchup history, and
/// concept-tag catalog sections.
/// </summary>
public sealed record ReviewSubjectDto(
    long GameId,
    ReviewHeaderDto Header,
    // 8-up stat strip (Damage / CS / Vision / Gold / Kill Part. / Dmg Taken /
    // Wards / KDA-style), mirroring ReviewViewModel.ApplyGameData. The laning@10
    // line rides on the header (LaningAt10Line / HasLaningAt10).
    IReadOnlyList<ReviewStatDto> Stats,
    // Active objectives that could be practiced this game (IObjectivesRepository
    // .GetActiveAsync). Read-only — the "mark practiced" toggle is DEFERRED.
    IReadOnlyList<ReviewObjectiveDto> Objectives,
    bool HasObjectives,
    // The existing saved review text fields on the game row (read-only display).
    ReviewFormDto Form,
    // Death audit: one row per DEATH event from the live kill feed, each with the
    // saved cause classification (if any) + the six selectable cause chips. The
    // chips ship with IsSelected reflecting the saved class — re-tap/classify is
    // a deferred WRITE; rendered read-only for now.
    IReadOnlyList<ReviewDeathDto> Deaths,
    bool HasDeaths,
    // Evidence triage: the attached-per-objective + main attached list (capped)
    // vs the "EVIDENCE TO SORT" unassigned inbox (uncapped). Read-only triage
    // rows (polarity/status/objective writes are DEFERRED).
    ReviewEvidenceDto Evidence,
    // Past notes for the same champion-vs-enemy matchup (newest-first), excluding
    // this game's own note. Read-only list.
    IReadOnlyList<ReviewMatchupHistoryDto> MatchupHistory,
    bool HasMatchupHistory,
    // The full concept-tag catalog with per-tag selection state for THIS game.
    // Replaces the old TagsJson passthrough on the form (kept there too for the
    // legacy chip preview). The selectable toggle grid is a DEFERRED write.
    IReadOnlyList<ReviewTagDto> TagCatalog,
    // P-027 "To sort" strip: THIS game's evidence that is fully untagged — no
    // objective AND no prompt (objective_id is null && prompt_id is null) and not
    // dismissed. The review renders these as a top-level strip so the user can
    // home each onto a prompt/objective. Always present (possibly empty), never
    // null. Coexists with Evidence (attached/unassigned) — both shapes ship.
    IReadOnlyList<ReviewPromptClipDto> UnsortedClips);

/// <summary>
/// Hero header for the reviewed game. Mirrors ReviewViewModel.ApplyGameData's
/// precomputed display strings (ResultText, KdaText, the meta line) and emits
/// *Hex colors rather than brushes.
/// </summary>
public sealed record ReviewHeaderDto(
    string ChampionName,
    string EnemyChampion,
    // Role-aware matchup heading ("Jinx vs Caitlyn", or a 2v2 pairing).
    string MatchupHeading,
    string GameRole,
    bool Win,
    // "VICTORY" / "DEFEAT".
    string ResultText,
    // "W" / "L".
    string WinLossText,
    string ResultColorHex,
    int Kills,
    int Deaths,
    int Assists,
    double KdaRatio,
    // "8 / 2 / 11".
    string KdaText,
    // "(9.50)".
    string KdaRatioText,
    string GameMode,
    string Duration,
    string DatePlayed,
    // "RANKED SOLO/DUO  ·  JUN 14, 20:42  ·  32:11" — same MetaLine convention
    // as the dashboard game rows.
    string MetaLine,
    // True when this game already has any persisted review signal
    // (mirrors DashboardViewModel.HasPersistedReview).
    bool HasReview,
    // v2.18 (schema v5): "LANING @10 // CS 84 · GOLD DIFF +210 · CS DIFF +6"
    // from the Match-V5 timeline backfill. Empty until the backfill has run.
    string LaningAt10Line,
    bool HasLaningAt10);

/// <summary>
/// One cell of the review stat strip. <see cref="Value"/> is the big number,
/// <see cref="Sub"/> the small caption under it (e.g. "7.2/min"); empty when
/// there's no caption. Mirrors the mockup's stat / stat-v / stat-s blocks.
/// </summary>
public sealed record ReviewStatDto(
    string Label,
    string Value,
    string Sub);

/// <summary>
/// One active objective shown as practiceable context for this game. Read-only:
/// the practiced toggle + execution note are MUTATIONS and DEFERRED. Colors are
/// *Hex strings mirroring DashboardSnapshotBuilder's objective level ramp.
/// </summary>
public sealed record ReviewObjectiveDto(
    long Id,
    string Title,
    string CompletionCriteria,
    string PhaseLabel,
    bool IsMini,
    bool IsPriority,
    string LevelName,
    int Score,
    int GameCount,
    int TargetGameCount,
    double Progress,
    string ProgressLabel,
    string LevelColorHex,
    string LevelDimColorHex,
    // "INGRAINING · PRE-GAME · 40 PTS" or the mini focus-drill variant.
    string MetaText,
    // The SAVED "practiced this game" state + execution note for THIS game, so the
    // review re-renders the toggle in its persisted state instead of always OFF.
    // (Hydrated from IObjectivesRepository.GetGameObjectivesAsync for the game.)
    bool Practiced,
    string ExecutionNote,
    // Custom coaching prompts the user authored for this objective (the guided
    // questions). Empty list when the objective has none. Each carries the
    // SAVED answer for this game (read-only display) — editable answer boxes are
    // a DEFERRED write.
    IReadOnlyList<ReviewPromptDto> Prompts,
    // P-027 no-prompt homing: THIS game's evidence tagged to THIS objective but
    // NOT to any prompt (objective_id == Id && prompt_id is null). The review
    // renders these as an "Objective evidence (no prompt)" sub-block so they're
    // not lost when a clip was attached to the objective but never bound to a
    // specific prompt. Always present (possibly empty), never null.
    IReadOnlyList<ReviewPromptClipDto> UnpromptedClips);

/// <summary>
/// A custom coaching prompt shown under an objective in the review, hydrated
/// with the user's SAVED answer for this game (empty when unanswered). The
/// editable answer box is a DEFERRED write — Answer is read-only display.
///
/// P-027: <see cref="Clips"/> carries THIS game's evidence rows tagged to this
/// prompt (prompt_id == Id), so the review groups clips under the prompt they
/// answer. Always present (possibly empty), never null.
/// </summary>
public sealed record ReviewPromptDto(
    long Id,
    string Phase,
    string Label,
    string Answer,
    IReadOnlyList<ReviewPromptClipDto> Clips);

/// <summary>
/// One clip/moment grouped under a prompt in the review (P-027). A flattened,
/// prompt-render-friendly slice of an evidence row tagged to the prompt: the
/// time text, note, jump-to start second, polarity accent, and the public share
/// link (revu.lol/&lt;id&gt;) when the backing bookmark has been uploaded
/// (empty otherwise). EvidenceId lets the frontend re-tag/untag the clip.
/// </summary>
public sealed record ReviewPromptClipDto(
    long EvidenceId,
    // "12:34" (or "12:34–13:02" range), empty when the row has no time.
    string TimeText,
    string Note,
    // Game-time seconds to jump the VOD to (0 when the row has no start time).
    int StartSeconds,
    string Polarity,
    // Polarity accent: bad → loss red, good → win green, else neutral accent.
    string PolarityColorHex,
    // Public share link (revu.lol/<id>) once the clip's bookmark was uploaded;
    // "" until shared (or for non-clip moments that have no bookmark).
    string ShareUrl);

/// <summary>
/// The review form's saved values, for READ-ONLY display. The frontend renders
/// these into disabled inputs. <see cref="Editable"/> is always false in v1 and
/// <see cref="EditingNote"/> carries the "review editing coming soon" copy.
/// Save/skip are DEFERRED mutations.
/// </summary>
public sealed record ReviewFormDto(
    bool Editable,
    string EditingNote,
    // 1–10 mental rating from the session log / review (0 when unset).
    int MentalRating,
    string MentalRatingColorHex,
    string WentWell,
    string Mistakes,
    string FocusNext,
    string ReviewNotes,
    string SpottedProblems,
    string Attribution,
    string PersonalContribution,
    string OutsideControl,
    string WithinControl,
    // The saved concept-tags JSON string off the game row (read-only passthrough;
    // the writable tag selector is DEFERRED). The structured catalog with
    // selection state ships on ReviewSubjectDto.TagCatalog.
    string TagsJson,
    // The saved FOCUS CHECK answer for this game (2=Yes / 1=Partly / 0=No;
    // null = unanswered) off session_log.focus_adherence. The write path persists
    // it (set_focus_adherence) but the read snapshot never carried it back, so the
    // gold selection vanished on re-render and never preselected on load
    // (P-028). renderFocus reads f.focusAdherence.
    int? FocusAdherence);

// ─────────────────────────────────────────────────────────────────────────────
// Death audit
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One DEATH event from the live kill feed (ReviewViewModel.LoadDeathAuditAsync),
/// joined with the saved cause classification for this (game, second). The six
/// cause chips mirror DeathClasses.All; exactly one IsSelected when classified.
/// Classification/clear are DEFERRED writes — rendered read-only for now.
/// </summary>
public sealed record ReviewDeathDto(
    int GameTimeSeconds,
    // "12:34" — mm:ss from the event game time.
    string TimeText,
    // The saved cause key (e.g. "vision"), empty when unclassified.
    string SelectedClass,
    // The saved cause label (e.g. "VISION"), empty when unclassified.
    string SelectedLabel,
    bool IsClassified,
    // The six cause chips in display order, with IsSelected on the saved one.
    IReadOnlyList<ReviewDeathChipDto> Chips);

/// <summary>One selectable death-cause chip. Key persists; Label/Hint display.</summary>
public sealed record ReviewDeathChipDto(
    string Key,
    string Label,
    string Hint,
    bool IsSelected);

// ─────────────────────────────────────────────────────────────────────────────
// Evidence triage
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The review's evidence triage split, mirroring ReviewViewModel's two lists:
/// the capped, prioritized "ATTACHED EVIDENCE" set (Attached) vs the uncapped
/// "EVIDENCE TO SORT" inbox of unassigned, non-dismissed moments (Unassigned).
/// The two are derived from the SAME GetForGameAsync row set so they can't drift
/// (P-013). Triage actions (polarity/status/objective) are DEFERRED writes.
/// </summary>
public sealed record ReviewEvidenceDto(
    IReadOnlyList<ReviewEvidenceItemDto> Attached,
    bool HasAttached,
    IReadOnlyList<ReviewEvidenceItemDto> Unassigned,
    bool HasUnassigned);

/// <summary>
/// One evidence/moment row in the triage list. Mirrors EvidenceInboxItem's
/// display fields; emits a polarity accent *Hex string the frontend applies.
/// </summary>
public sealed record ReviewEvidenceItemDto(
    long Id,
    string SourceKind,
    int? StartTimeSeconds,
    int? EndTimeSeconds,
    // "12:34" (or "12:34–13:02" when there's a range), empty when no time.
    string TimeText,
    string Title,
    string Note,
    long? ObjectiveId,
    string ObjectiveTitle,
    string Polarity,
    string Status,
    // Polarity accent: bad → loss red, good → win green, else neutral accent.
    string PolarityColorHex,
    // "needs review" / "evidence" / "highlight" — human label for the status.
    string StatusLabel);

// ─────────────────────────────────────────────────────────────────────────────
// Matchup history
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One past note for the same champion-vs-enemy matchup (newest-first), excluding
/// this game's own note. Mirrors ReviewViewModel.MatchupHistoryItem.
/// </summary>
public sealed record ReviewMatchupHistoryDto(
    string Note,
    bool? Helpful,
    // "Game 5581701721  ·  Jun 12, 2026 18:04  ·  Helpful" — same MetaText
    // convention as ReviewViewModel.BuildMatchupMetaText.
    string MetaText);

// ─────────────────────────────────────────────────────────────────────────────
// Concept-tag catalog
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One concept tag from the catalog with its selection state for THIS game.
/// Mirrors ReviewTagState (IConceptTagRepository.GetAllAsync + GetIdsForGameAsync).
/// The toggle grid is a DEFERRED write — IsSelected ships read-only.
/// </summary>
public sealed record ReviewTagDto(
    long Id,
    string Name,
    string Polarity,
    string ColorHex,
    bool IsSelected);

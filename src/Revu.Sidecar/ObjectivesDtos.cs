#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/objectives.
//
// These reproduce the WinUI ObjectivesViewModel's display shape WITHOUT any
// XAML/WinUI types: there are NO SolidColorBrush properties — every color is a
// plain *Hex string the Tauri frontend resolves itself. Property names are
// PascalCase here; the serializer is configured with
// JsonNamingPolicy.CamelCase in Program.cs, so the wire shape is camelCase and
// matches desktop/ui/sample-objectives.json verbatim.
//
// READ-ONLY: this is a pure read slice. The create-objective FORM and every
// mutation (edit/delete/complete/set-priority) are DEFERRED — the contract
// exposes a CreateForm descriptor flagged `enabled: false` so the frontend can
// render-but-disable it. No write path is reachable through this endpoint.
//
// Null vs empty:
//   - Collections are always present (possibly empty), never null.
//   - scoreHistory / champions are always arrays (possibly empty).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level objectives snapshot returned by GET /api/objectives.</summary>
public sealed record ObjectivesDto(
    string GeneratedAt,
    bool HasObjectives,
    bool HasActiveObjectives,
    bool HasCompletedObjectives,
    bool HasFocusObjectives,
    bool HasSpottedProblems,
    IReadOnlyList<ObjectiveCardDto> ActiveObjectives,
    IReadOnlyList<ObjectiveCardDto> FocusObjectives,
    IReadOnlyList<CompletedObjectiveDto> CompletedObjectives,
    IReadOnlyList<SpottedProblemDto> SpottedProblems,
    // Mutation surface is DEFERRED in the read-only phase. The frontend renders
    // this form but keeps it disabled; `enabled` is the stable kill-switch.
    CreateFormDto CreateForm,
    // ── Create-form picker data (the NEW-objective form needs these up front;
    //    EDIT hydrates per-objective via GET /api/objective?id=N) ──────────────
    // Champions the user actually plays (newest first) for the picker typeahead.
    IReadOnlyList<string> PlayedChampions,
    // Criteria-metric dropdown options (index 0 = "Free text only", then the
    // declared metrics). Lets the create form build its dropdown without drift.
    IReadOnlyList<CriteriaMetricOptionDto> CriteriaMetrics);

/// <summary>
/// One active objective card (mastery ladder, mental, or mini focus drill).
/// Mirrors ObjectiveDisplayItem's precomputed display fields. The ring fill is
/// driven by <see cref="Progress"/> (0..1) + <see cref="LevelColorHex"/>, the
/// same animated ring the dashboard uses.
/// </summary>
public sealed record ObjectiveCardDto(
    long Id,
    string Title,
    string SkillArea,
    // "primary" | "mental" | "mini" (raw type column).
    string Type,
    // Single-phase display label via ObjectivePhases.ToDisplayLabel (e.g. "Pre-Game").
    string PhaseLabel,
    // Compact multi-phase summary (e.g. "PRE + IN", "ALL PHASES") — mirrors
    // ObjectiveDisplayItem.PhasesSummary. Prefer this for the card's phase chip.
    string PhasesSummary,
    bool IsMini,
    bool IsMental,
    int Score,
    int GameCount,
    int TargetGameCount,
    double Progress,
    string LevelName,
    string LevelColorHex,
    string LevelDimColorHex,
    bool IsPriority,
    // Ready-to-show one-liner under the title.
    string InfoText,
    // Cumulative score per game, oldest→newest, for the sparkline. Always an array.
    IReadOnlyList<int> ScoreHistory,
    bool HasScoreHistory,
    // Champion gate. Empty = applies to all champions. Always an array.
    IReadOnlyList<string> Champions,
    string ChampionsSummary,
    // All-caps meta strip ("INGRAINING · PRE-GAME · 40 PTS" or focus framing).
    string MetaText,
    // v2.17.7 mini progress badge ("2 of 3 games"), "" for primary.
    string FocusProgressText,
    // ── v2.18 measured-criterion hit-rate (mirror ObjectiveDisplayItem) ──────
    // True when the objective has a structured criterion (non-empty metric);
    // gates the hit-rate chip. False for free-text-only objectives.
    bool HasStructuredCriteria,
    // Raw counts from GetCriteriaHitRateAsync; Evaluated counts only games where
    // the criterion actually ran.
    int CriteriaHits,
    int CriteriaEvaluated,
    // Ready-to-show chip text: "HIT x/y GAMES" when measured, "NOT MEASURED YET"
    // when no game has been evaluated, "" when no structured criterion.
    string CriteriaHitRateText,
    // Chip color: muted when 0 evaluated, positive when hits*2>=evaluated, else
    // negative. No SolidColorBrush — a plain *Hex string.
    string CriteriaHitRateHex,
    // The criterion sentence ("Success: CS per minute ≥ 7" or free-text). Empty
    // when the objective has neither a structured criterion nor free text.
    string CriteriaText,
    bool HasCriteriaText);

/// <summary>One completed objective (collapsed row).</summary>
public sealed record CompletedObjectiveDto(
    long Id,
    string Title,
    string PhaseLabel,
    int Score,
    int GameCount,
    // "40 pts · 8 games" — mirrors CompletedObjectiveItem.SummaryText.
    string SummaryText);

/// <summary>
/// A recent spotted-problem note shown as backlog context for future objectives.
/// Mirrors SpottedProblemItem; <see cref="ResultText"/> is derived W/L.
/// </summary>
public sealed record SpottedProblemDto(
    long GameId,
    string ChampionName,
    string EnemyChampion,
    string DatePlayed,
    string ProblemText,
    bool Win,
    // "W" | "L".
    string ResultText,
    // "Kai'Sa vs Tristana" when enemy known, else just "Kai'Sa".
    string ChampionDisplay,
    // Win → win green, loss → loss red. No SolidColorBrush.
    string ResultColorHex);

/// <summary>
/// Descriptor for the create-objective form. DEFERRED: the form is rendered by
/// the frontend but disabled in the read-only phase. <see cref="Enabled"/> is
/// the stable flag the frontend keys off; <see cref="TodoNote"/> documents why.
/// </summary>
public sealed record CreateFormDto(
    bool Enabled,
    string Note,
    string TodoNote);

// ─────────────────────────────────────────────────────────────────────────────
// Edit-hydration DTOs for GET /api/objective?id=N.
//
// Mirrors ObjectivesViewModel.BeginEditObjectiveAsync: returns the FULL editable
// state for one objective (core fields + multi-phase flags + structured criterion
// + focus-phase + custom prompts + champion gate) plus the played-champion list
// the picker uses for typeahead. Every index/key matches the WinUI form's pickers
// so the frontend submit can echo them back to POST /api/objective/create|update.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Full editable hydration of one objective for the Edit form.</summary>
public sealed record ObjectiveEditDto(
    long Id,
    string Title,
    string SkillArea,
    // Raw type column ("primary" | "mental" | "mini"); the frontend maps to its
    // type select. TypeIndex is the WinUI picker index (0 primary / 1 mental / 2 mini).
    string Type,
    int TypeIndex,
    // Active vs completed: completed objectives still edit via the same form.
    string Status,
    bool IsActive,
    string CompletionCriteria,
    string Description,
    bool PracticePre,
    bool PracticeIn,
    bool PracticePost,
    int TargetGameCount,
    // Focus-phase picker: 0 Auto / 1 Laning / 2 Mid-late / 3 Teamfight / 4 Any.
    int FocusPhaseIndex,
    // Structured criterion. MetricIndex 0 = "Free text only"; 1..N map to
    // ObjectiveCriteria.Metrics[index-1]. OpIndex 0 = ">=" (at least), 1 = "<=".
    int CriteriaMetricIndex,
    int CriteriaOpIndex,
    string CriteriaValueText,
    // Custom prompts (ordered by sortOrder). Each carries its DB id so the diff-
    // save can update-vs-insert; the frontend echoes id back on submit.
    IReadOnlyList<PromptDraftDto> Prompts,
    // Champion gate (objective applies only to these; empty = all champions).
    IReadOnlyList<string> Champions,
    // Champions the user actually plays (newest first), for the picker typeahead.
    IReadOnlyList<string> PlayedChampions,
    // Event-token gate: the trackable tokens this objective is tied to (raw event
    // types, SPELL_* spells, TEAMFIGHT). Empty = tracks no events.
    IReadOnlyList<string> EventTypes,
    // The full catalog of selectable tokens (token + group + label + color) so the
    // edit UI can render the grouped picker without hardcoding the vocabulary.
    IReadOnlyList<EventTokenOptionDto> EventTypeOptions);

/// <summary>One selectable event token for the objective-edit picker.</summary>
public sealed record EventTokenOptionDto(
    string Token,
    string Group,
    string Label,
    string Color);

/// <summary>One custom-prompt row for the edit form (id + phase + label).</summary>
public sealed record PromptDraftDto(
    // DB row id, so the diff-save updates instead of re-inserting. 0/absent = new.
    long Id,
    // "pregame" | "ingame" | "postgame" (normalized).
    string Phase,
    // WinUI PhaseIndex (0 pre / 1 in / 2 post) for the per-row phase select.
    int PhaseIndex,
    string Label);

/// <summary>
/// One criteria-metric picker option for the measured-criterion dropdown.
/// Mirrors ObjectivesViewModel.BuildCriteriaMetricOptions: index 0 is always
/// "Free text only"; 1..N are <see cref="Revu.Core.Services.ObjectiveCriteria"/>
/// metrics in declared order. Sent inside <see cref="ObjectiveFormMetaDto"/> so
/// the form can build its dropdowns without hardcoding the metric list.
/// </summary>
public sealed record CriteriaMetricOptionDto(
    int Index,
    // Stable metric key ("" for the free-text option); echoed nowhere — the
    // frontend submits the INDEX and the sidecar resolves the key server-side.
    string Key,
    string Label,
    // true flips the default comparator to "at most" (e.g. deaths).
    bool LowerIsBetter);

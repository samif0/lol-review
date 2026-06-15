#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/pregame (the champ-select / in-game intel deck).
//
// Same conventions as the other *Dtos.cs: PascalCase here, camelCase on the wire
// (JsonNamingPolicy.CamelCase in Program.cs). NO brushes — every accent is a
// plain *Hex string the frontend resolves itself.
//
// This is the STATIC pre-game intel the page needs at load: the rotating intel
// deck, the active/priority objectives + their pre-game custom prompts, last
// matchup notes, the intent carry-over seeds (carry / objective / adherence) with
// provenance, the latest if-then plan, and the mood/intention gates. The LIVE
// champ-select data (my champ / enemy / role / participant map → live matchup +
// 2v2 pairing) arrives separately over the SSE channel (GET /api/events,
// ChampSelectStarted/Updated events) so the deck reacts without re-fetching.
//
// Mirrors PreGameDialogViewModel.LoadAsync (the read half) — the DEFERRED writes
// (mood, intent, practiced toggles) are captured into session_log etc. at game
// END by the sidecar's hosted GameMonitorService → GameEnded consumer, exactly
// like ShellViewModel does in the WinUI app. So this endpoint is READ-ONLY.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level pre-game intel snapshot returned by GET /api/pregame.</summary>
public sealed record PreGameDto(
    string GeneratedAt,
    // Optional champ-select context the caller passed (?myChampion=&enemy=&role=).
    // Empty when the page loaded before champ select / outside a game.
    string MyChampion,
    string EnemyChampion,
    string MyPosition,
    // Rotating INTEL deck (priority objective, last game, matchup notes, prior
    // pre-game answers, enemy cooldowns). Empty list ⇒ frontend hides the rotator.
    IReadOnlyList<IntelCardDto> IntelDeck,
    // Live matchup card seed (champ vs enemy). HasMatchupDetected when my champ
    // is known; the enemy column shows "…" until the enemy locks (over SSE).
    PreGameMatchupDto Matchup,
    // Saved "YOUR NOTES vs ENEMY" for the detected matchup. Empty when none.
    PreGameMatchupHistoryDto MatchupHistory,
    // THIS GAME'S INTENT carry-over card: the zero-tap seed + provenance + the
    // selectable source chips (carry / priority objective / lowest adherence).
    PreGameIntentDto Intent,
    // Latest Tilt-Fix if-then plan (≤14d), display-only. Null when none.
    string? ActivePlan,
    // MENTAL mood selector gate. Always true in v2.18 (un-gated from Tilt Fix).
    bool ShowMoodSelector,
    // Session-intention (first-game-of-day) gate + existing value.
    PreGameSessionIntentionDto SessionIntention,
    // Active pre-game objectives (priority headline + practiced toggles) + their
    // custom pre-game prompt blocks. Champion-gated when MyChampion is known.
    PreGameObjectivesDto Objectives,
    IReadOnlyList<PreGamePromptBlockDto> PromptBlocks,
    // Accent palette (mirrors the WinUI AccentGold/Teal/Purple/Blue brushes).
    string GoldHex,
    string TealHex,
    string PurpleHex,
    string BlueHex);

/// <summary>One rotating intel card (eyebrow + headline + body).</summary>
public sealed record IntelCardDto(string Eyebrow, string Headline, string Body);

/// <summary>Live matchup card seed. The frontend overlays SSE champ-select
/// updates (live enemy/role/pairing) on top of this static base.</summary>
public sealed record PreGameMatchupDto(
    bool HasMatchupDetected,
    string MyChampion,
    // "…" placeholder when the enemy laner hasn't locked yet.
    string EnemyOrPlaceholder,
    string AccentHex);

/// <summary>Saved matchup notes ("YOUR NOTES vs ENEMY").</summary>
public sealed record PreGameMatchupHistoryDto(
    bool Has,
    // "YOUR NOTES vs ENEMY" header, or empty when no notes.
    string HeaderText,
    IReadOnlyList<PreGameMatchupNoteDto> Items,
    string AccentHex);

public sealed record PreGameMatchupNoteDto(
    string Note,
    // "MMM d" from CreatedAt unix (empty when no date).
    string DateText,
    bool WasHelpful,
    bool HasHelpfulRating);

/// <summary>THIS GAME'S INTENT carry-over card.</summary>
public sealed record PreGameIntentDto(
    // The zero-tap default text (carry seed if present, else priority-objective
    // seed, else ""). The frontend prefills the intent box with this.
    string SeedText,
    // Provenance line above the box (e.g. "FROM YOUR LAST REVIEW — KAI'SA (L) · …").
    string Provenance,
    // The selected source the seed came from: "carry" | "objective" | "adherence"
    // | "" (none). Drives which chip starts highlighted.
    string SelectedSource,
    // Source chips (only present when their seed exists, mirroring Has*Source).
    bool HasCarrySource,
    string CarrySeed,
    string CarryProvenance,
    bool HasObjectiveSource,
    string ObjectiveSeed,
    string ObjectiveProvenance,
    // Data-gated until ~July 2026 (≥10 evaluated criteria rows). Hidden when false.
    bool HasAdherenceSource,
    string AdherenceSeed,
    string AdherenceProvenance,
    string AccentHex);

/// <summary>Session-intention (first-game-of-day) section state.</summary>
public sealed record PreGameSessionIntentionDto(
    // ShowIntention = TiltFixEnabled && IsFirstGame.
    bool Show,
    bool IsFirstGame,
    // Existing intention text for today (empty when none).
    string Intention,
    // The 3 if-then preset chips (QuickIntentionOptions).
    IReadOnlyList<string> QuickOptions,
    string AccentHex);

/// <summary>Active pre-game objectives mega-card.</summary>
public sealed record PreGameObjectivesDto(
    bool HasActiveObjective,
    // PRIORITY headline (priority objective ?? first active).
    string PriorityTitle,
    string PriorityCriteria,
    bool HasObjectives,
    // "PRACTICED THIS GAME?" rows (practiced toggles, captured at EOG).
    IReadOnlyList<PreGameObjectiveRowDto> Items,
    string AccentHex);

public sealed record PreGameObjectiveRowDto(
    long ObjectiveId,
    string Title,
    string Criteria,
    bool IsPriority);

/// <summary>One objective's pre-game custom prompts (answer boxes carry over via
/// the session_key draft path — auto-saved on every keystroke, promoted at EOG).</summary>
public sealed record PreGamePromptBlockDto(
    long ObjectiveId,
    string ObjectiveTitle,
    bool IsPriority,
    // "PRIORITY" (gold) / "ACTIVE" (teal).
    string Eyebrow,
    string AccentHex,
    IReadOnlyList<PreGamePromptDto> Prompts);

public sealed record PreGamePromptDto(
    long PromptId,
    string Label,
    // Pre-filled draft answer staged under the current champ-select session key.
    string AnswerText);

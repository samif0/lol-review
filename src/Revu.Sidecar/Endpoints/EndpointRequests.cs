#nullable enable

using System.Text.Json;

// ── Write-endpoint request bodies ────────────────────────────────────────────
// v3.3: WithCoach tags the block as run with the coach present (its games are
// reviewed with the coach outside Revu and leave the review queue). WithCoach
// null = "leave today's tag as it is": the sessions upsert writes with_coach
// unconditionally, so an {intention}-only post (the champ-select session box,
// older clients) must not silently retag a with-coach day to solo.
internal sealed record StartBlockBody(string Intention, bool? WithCoach = null);
// Date is the open block's own date (from IntentDto.BlockDate) so a carried-over
// block from a prior day closes the right row. Null/empty falls back to today.
internal sealed record EndBlockBody(int Rating, string? Note, string? Date = null);
// v3.3: coaching stint start. PlannedEndDate is optional strict yyyy-MM-dd.
internal sealed record StartStintBody(string Name, string? PlannedEndDate = null);
internal sealed record RestoreBackupBody(string BackupFilePath);
internal sealed record GameIdBody(long GameId);
internal sealed record ObjectiveIdBody(long Id);

// ── Review-page granular write bodies (Batch 2) ──────────────────────────────
// Shared evidence triage (Review + VOD): polarity, objective-attach, status.
internal sealed record EvidencePolarityBody(long EvidenceId, string? Polarity);
// ObjectiveId null/<=0 detaches; GameId (optional) lets attach also mark the
// objective practiced for that game (mirrors the WinUI evidence-attach flow).
internal sealed record EvidenceObjectiveBody(long EvidenceId, long? ObjectiveId, long? GameId);
// P-027: PromptId null/<=0 detaches. Tags the evidence row to a custom prompt
// (independent of objective_id) so the review groups clips under the prompt.
internal sealed record EvidencePromptBody(long EvidenceId, long? PromptId);
internal sealed record EvidenceStatusBody(long EvidenceId, string? Status);
// Per-death cause classification, keyed on (gameId, timeS).
internal sealed record DeathClassifyBody(long GameId, int TimeS, string Key);
internal sealed record DeathClearBody(long GameId, int TimeS);
// Per-objective custom-prompt answer (empty Text deletes the row).
internal sealed record PromptAnswerBody(long PromptId, long GameId, string? Text);
// Per-game focus adherence: 2=Yes / 1=Partly / 0=No; null clears.
internal sealed record FocusAdherenceBody(long GameId, int? Value);

// ── VOD bookmark CRUD bodies (Batch 2) ───────────────────────────────────────
// Quick note-bookmark add (no clip fields — ffmpeg deferred to Batch 3).
internal sealed record AddBookmarkBody(long GameId, int TimeS, string? Note, long? ObjectiveId, long? PromptId);
internal sealed record BookmarkIdBody(long BookmarkId);
internal sealed record BookmarkNoteBody(long BookmarkId, string? Note);
internal sealed record BookmarkObjectiveBody(long BookmarkId, long? ObjectiveId);
internal sealed record BookmarkTagBody(long BookmarkId, long? ObjectiveId, long? PromptId);
internal sealed record BookmarkQualityBody(long BookmarkId, string? Quality);

// ── Manual game entry bodies (Batch 2) ───────────────────────────────────────
// One objective assessment row (practiced toggle + execution note).
internal sealed record ManualObjectiveBody(long ObjectiveId, bool Practiced, string? ExecutionNote);
// The whole Manual Entry form. MentalRating reaches LogGameAsync only.
internal sealed record ManualGameBody(
    string ChampionName,
    bool Win,
    int Kills,
    int Deaths,
    int Assists,
    int MentalRating,
    string? GameMode,
    string? Notes,
    string? Mistakes,
    string? WentWell,
    string? FocusNext,
    List<ManualObjectiveBody>? Objectives);

internal sealed record ObjectivePracticeBody(long ObjectiveId, bool Practiced, string? ExecutionNote);

internal sealed record SaveReviewBody(
    long GameId,
    string? ChampionName,
    bool Win,
    int MentalRating,
    string? WentWell,
    string? Mistakes,
    string? FocusNext,
    string? ReviewNotes,
    string? ImprovementNote,
    string? Attribution,
    string? MentalHandled,
    string? SpottedProblems,
    string? OutsideControl,
    string? WithinControl,
    string? PersonalContribution,
    string? EnemyLaner,
    string? MatchupNote,
    List<long>? SelectedTagIds,
    // Free-text concept tags typed in the review tag input. Resolved to catalog tag
    // ids (find-or-create by name) at save time and merged into SelectedTagIds, so a
    // tag the user typed actually persists (it had nowhere to go before).
    List<string>? FreeTextTags,
    List<ObjectivePracticeBody>? ObjectivePractices,
    int? FocusAdherence);

// One custom-prompt row in a create/update objective body. Id 0/absent = a NEW
// prompt (insert); a non-zero Id refers to an existing row the diff-save updates
// (or, if absent from the list, deletes). Phase is "pregame"|"ingame"|"postgame".
internal sealed record ObjectivePromptBody(long Id, string? Phase, string? Label);

// POST /api/objective/create body. Beyond the core fields it now carries the full
// editing surface: custom prompts (diff-saved), champion gate (replaced wholesale,
// empty = all champs), focus-phase picker index, and the structured criterion
// (metric/op picker indices + value text). Mirrors ObjectivesViewModel form state.
internal sealed record CreateObjectiveBody(
    string Title, string? SkillArea, string? Type,
    string? CompletionCriteria, string? Description,
    bool PracticePre, bool PracticeIn, bool PracticePost,
    int TargetGameCount,
    List<ObjectivePromptBody>? Prompts,
    List<string>? Champions,
    int FocusPhaseIndex,
    int CriteriaMetricIndex,
    int CriteriaOpIndex,
    string? CriteriaValueText,
    // Trackable event tokens the objective is tied to (raw types, SPELL_*, TEAMFIGHT).
    List<string>? EventTypes = null);

// POST /api/objective/update body. Same editing surface as create, plus the id
// and the explicit target-game-count (minis only; primary/mental force 0).
internal sealed record UpdateObjectiveBody(
    long Id, string Title, string? SkillArea, string? Type,
    string? CompletionCriteria, string? Description,
    bool PracticePre, bool PracticeIn, bool PracticePost,
    int TargetGameCount,
    List<ObjectivePromptBody>? Prompts,
    List<string>? Champions,
    int FocusPhaseIndex,
    int CriteriaMetricIndex,
    int CriteriaOpIndex,
    string? CriteriaValueText,
    List<string>? EventTypes = null);

internal sealed record ResetBody(
    string Emotion, int IntensityBefore, int? IntensityAfter,
    string? ReframeThought, string? ReframeResponse, string? IfThenPlan);

// POST /api/config/save body. EVERY field is nullable: null = "leave unchanged"
// (read-modify-write only mutates the fields the frontend actually sends, so a
// partial save from the Settings page never clobbers unrelated config keys).
internal sealed record SaveConfigBody(
    string? ClipsFolder,
    int? ClipsMaxSizeMb,
    bool? BackupEnabled,
    string? BackupFolder,
    bool? TiltFixMode,
    bool? RequireReviewNotes,
    bool? SidebarAnimationEnabled,
    bool? MinimizeDuringGame,
    bool? AutoTimelineClippingEnabled,
    bool? AutoTimelineClippingHintDismissed,
    bool? AutoClipObjectivesEnabled,
    string? FirstReviewTutorialStep,
    bool? FirstReviewTutorialCompleted,
    bool? FirstReviewTutorialDismissed,
    long? FirstReviewTutorialObjectiveId,
    long? FirstReviewTutorialGameId,
    string? RiotId,
    string? Region,
    string? PrimaryRole,
    // Onboarding role-finish (skip path) stamps OnboardingSkipped=true; null leaves
    // it unchanged. Login path never sends it (resolve already set it false).
    bool? OnboardingSkipped,
    // Main-window size: "default" | "maximized" | "WxH" (validated by
    // ConfigSaveGuards.TryResolveWindowResolution); null/blank = unchanged.
    string? WindowResolution = null,
    string? AscentFolder = null);

// POST /api/review/draft/save body — same shape as SaveReviewBody minus the
// championName/win/requireReviewNotes fields a finalized save needs (a draft
// only carries the gameId + the in-progress ReviewSnapshot).
internal sealed record SaveReviewDraftBody(
    long GameId,
    int MentalRating,
    string? WentWell,
    string? Mistakes,
    string? FocusNext,
    string? ReviewNotes,
    string? ImprovementNote,
    string? Attribution,
    string? MentalHandled,
    string? SpottedProblems,
    string? OutsideControl,
    string? WithinControl,
    string? PersonalContribution,
    string? EnemyLaner,
    string? MatchupNote,
    List<long>? SelectedTagIds,
    // Free-text concept tags typed in the review tag input. Resolved to catalog tag
    // ids (find-or-create by name) at save time and merged into SelectedTagIds, so a
    // tag the user typed actually persists (it had nowhere to go before).
    List<string>? FreeTextTags,
    List<ObjectivePracticeBody>? ObjectivePractices,
    int? FocusAdherence);

// ── Rules CRUD request bodies ─────────────────────────────────────────────────
// RuleType is one of: custom | no_play_day | no_play_after | loss_streak |
// max_games | min_mental (null/blank → custom). ConditionValue is the raw stored
// value the per-type formatter reads; for loss_streak the frontend encodes the
// optional cooldown as "threshold:minutes". ReplacementPlan is the P2c "then I
// will…" plan (optional). All trimmed server-side.
internal sealed record RuleIdBody(long Id);

// v3.7 hard stop.
internal sealed record RuleEnforceBody(long Id, bool Enforce);

internal sealed record HardStopOverrideBody(long RuleId);

internal sealed record CreateRuleBody(
    string Name,
    string? RuleType,
    string? ConditionValue,
    string? Description,
    string? ReplacementPlan);

internal sealed record UpdateRuleBody(
    long Id,
    string Name,
    string? RuleType,
    string? ConditionValue,
    string? Description,
    string? ReplacementPlan);

// ── Clip extraction body (Batch 3) ───────────────────────────────────────────
// VodPath + ChampionName ride along from the loaded VOD snapshot (the frontend
// has both), mirroring the WinUI VM which reads them from its loaded state. The
// range is clamped/ordered server-side. Quality is good|neutral|bad (or blank).
internal sealed record ExtractClipBody(
    long GameId,
    string VodPath,
    string? ChampionName,
    int StartTimeS,
    int EndTimeS,
    string? Note,
    string? Quality,
    long? ObjectiveId,
    long? PromptId);

// POST /api/clip/auto-objectives body. GameId is required; ObjectiveId (optional)
// restricts to the framed objective's events (null = all active-objective-tied).
internal sealed record AutoObjectiveClipsBody(
    long GameId,
    long? ObjectiveId);

// ── Pattern review bodies (Batch 3) ───────────────────────────────────────────
// Mark a cross-game pattern reviewed. Kind + MomentCount come from the loaded
// /api/patterns snapshot; both optional so the server can re-resolve them.
internal sealed record MarkPatternReviewedBody(string PatternKey, string? Kind, int? MomentCount);

// Per-moment note autosave (+ silent one-time clip). EvidenceId + Text are
// required; the clip fields ride from the loaded snapshot. AlreadyClipped lets the
// frontend suppress re-extraction once a moment has a clip (mirrors HasClip gate).
internal sealed record PatternMomentNoteBody(
    long EvidenceId,
    string? Text,
    long? GameId,
    string? ChampionName,
    string? VodPath,
    string? Title,
    string? Polarity,
    int? StartTimeS,
    int? EndTimeS,
    bool AlreadyClipped);

// ── Riot auth / account bodies (Batch 4) ─────────────────────────────────────
// Email-OTP login. The proxy sends the code; nothing persists until /verify.
internal sealed record AuthLoginBody(string Email);
// Sign-up = login + an invite code (upper-cased server-side).
internal sealed record AuthSignupBody(string Email, string InviteCode);
// Verify the OTP and persist the session. Email rides along so we stamp the right
// RiotSessionEmail (the proxy's verify response carries only token + expiry).
internal sealed record AuthVerifyBody(string Code, string? Email);
// Resolve the Riot ID → PUUID using the stored session; persists id/region/puuid.
internal sealed record AuthResolveBody(string RiotId, string Region);

// ── Clip share body (Batch 4) ────────────────────────────────────────────────
// The frontend sends only gameId + bookmarkId (+ optional caption); the sidecar
// resolves the clip path / champion / share-state from the bookmark server-side.
internal sealed record ShareClipBody(long GameId, long BookmarkId, string? ChampionName, string? Title);
internal sealed record DeleteClipBody(long GameId, long BookmarkId);

// ── Pre-game deferred-snapshot bodies (Batch 5 / LCU) ────────────────────────
// These stage champ-select choices into LcuLiveState; the SidecarGameFlowCoordinator
// persists them to session_log at game END (mirror the PreGameDialogViewModel
// statics → ShellViewModel EOG hop). The draft body is the ONE that writes to the
// DB (a pre_game_draft_prompts upsert under the live session key).
internal sealed record PreGameMoodBody(int Mood);
internal sealed record PreGameIntentBody(string? Intention, string? Source, bool Cleared);
internal sealed record PreGamePracticedBody(List<long>? ObjectiveIds);
internal sealed record PreGameDraftBody(long PromptId, string? Text);
internal sealed record PreGameIfThenBody(string? Plan);

internal sealed record SaveEncounterBody(long GameId, int? EventId, string RequestId, int StartS, int EndS, string Classification, string? Note);

// ── v3.11 Event corrections bodies ───────────────────────────────────────────
// Subject = the event as the page saw it (event_key preferred, id as fallback, type +
// time cross-checked server-side); Patch = the change (attrs is a JSON object of
// scalar values). Both null for the ops that need none (add has no subject;
// remove / confirm have no patch). Reason <= 280 characters after trim.
internal sealed record CorrectionSubjectBody(string? EventKey, long? EventId, string? Type, int? TimeS);
internal sealed record CorrectionPatchBody(string? EventType, int? GameTimeS, int? EndS, JsonElement? Attrs);
internal sealed record SaveCorrectionBody(long GameId, string? CorrectionId, string? Op,
    CorrectionSubjectBody? Subject, CorrectionPatchBody? Patch, string? Reason);
internal sealed record RevertCorrectionBody(long GameId, string? CorrectionId, string? Reason);

// ── v3.9 Matchup journal bodies ──────────────────────────────────────────────
// Champion lists arrive as JSON arrays of display names in slot order (1v1 for
// top / mid, jungler + mid for jungle, adc + support for bot / support); the
// repository canonicalizes and validates them. Prior / Observed null = empty on
// create / update, and "leave unchanged" on the inline notes write.
internal sealed record CreateMatchupBody(
    string? Lane,
    List<string?>? AllyChamps,
    List<string?>? EnemyChamps,
    string? Prior,
    string? Observed,
    long? GameId);

internal sealed record UpdateMatchupBody(
    long Id,
    string? Lane,
    List<string?>? AllyChamps,
    List<string?>? EnemyChamps,
    string? Prior,
    string? Observed);

internal sealed record MatchupNotesBody(long Id, string? Prior, string? Observed);

internal sealed record MatchupIdBody(long Id);

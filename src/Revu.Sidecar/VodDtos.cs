#nullable enable

namespace Revu.Sidecar;

/// <summary>Response for GET /api/vod?gameId=N — the VOD file + its bookmarks.</summary>
public sealed record VodDto(
    string GeneratedAt,
    bool HasVod,
    long GameId,
    // Absolute path to the recording on disk; the frontend converts it to an
    // asset: URL via Tauri convertFileSrc to feed an HTML <video>. Empty if none.
    string FilePath,
    string FileName,
    // Header info for the player chrome.
    string ChampionName,
    string EnemyChampion,
    string ResultText,
    string ResultColorHex,
    string GameMode,
    string DatePlayed,
    int GameDurationSeconds,
    IReadOnlyList<VodBookmarkDto> Bookmarks,
    // Live timeline events (kills/deaths/objectives) → colored EVENT TIMELINE
    // markers. From IGameEventsRepository.GetEventsAsync, sorted by game time.
    IReadOnlyList<VodEventDto> GameEvents,
    // The 'Moments to Review' inbox: auto-detected timeline moments + saved clips,
    // split out of IEvidenceRepository.GetForGameAsync so the 3-way Auto/Clips/
    // Bookmarks filter can render each lane. Bookmarks come from Bookmarks above.
    IReadOnlyList<VodEvidenceDto> AutoMoments,
    IReadOnlyList<VodEvidenceDto> SavedClips,
    // v3.11: the correctable event types (per-type attribute chips) the fix panel
    // builds its selects from, and this game's corrections ledger (newest first).
    // The builder always fills both; Empty() passes empty lists.
    IReadOnlyList<VodEventTypeDto>? EventTypeCatalog = null,
    IReadOnlyList<VodCorrectionDto>? Corrections = null);

/// <summary>A timeline marker (moment) on the VOD.</summary>
public sealed record VodBookmarkDto(
    long Id,
    int GameTimeSeconds,
    string TimeLabel,        // "12:41"
    string Note,
    string TagsJson,
    bool HasClip,
    int? ClipStartSeconds,
    int? ClipEndSeconds,
    // Objective this bookmark/clip is tagged to (null = untagged). Lets a clip-only
    // bookmark row pre-select its objective in the VOD player's objective picker.
    long? ObjectiveId = null,
    // P-027: optional custom-prompt tag (from VodBookmarkRecord.PromptId). When set,
    // the VOD player renders a small prompt badge on the bookmark/clip row; null when
    // the bookmark answers no prompt. Surfaced so the picker's saved choice is visible.
    long? PromptId = null,
    // Public share link (revu.lol/<id>) once the clip has been uploaded; "" until
    // shared. Drives the VOD player's Share-button label (Share vs Copy link).
    // The clip PATH itself stays server-side (resolved on POST /api/clip/upload).
    string ShareUrl = "");

/// <summary>
/// A live in-game event placed on the EVENT TIMELINE (kills/deaths/objectives).
/// Kind buckets the event into the win/loss/gold/neutral marker language the
/// timeline colors by; ColorHex carries the exact per-type hue from the WinUI
/// TimelineEvent palette.
/// </summary>
public sealed record VodEventDto(
    long Id,
    string EventType,        // "KILL" | "DEATH" | "DRAGON" | …
    int GameTimeSeconds,
    string TimeLabel,        // "12:41"
    string ShortLabel,       // "KIL" | "DTH" | "DRG" …
    string Label,            // "Kill" | "Death" | "Dragon" …
    string Summary,          // parsed from Details JSON (e.g. victim/killer), may be ""
    string Kind,             // "win" | "loss" | "gold" | "neutral" — marker color bucket
    string ColorHex,         // exact per-type hex
    // Objective tie: set when this event's token (raw type, SPELL_*, or membership in
    // a tracked TEAMFIGHT) matches an ACTIVE objective. Drives the timeline priority
    // lane — tied events take position + label priority over untied markers.
    // ObjectiveId is the FIRST matching objective (back-compat / priority-lane color).
    long? ObjectiveId = null,
    string ObjectiveTitle = "",
    string ObjectiveColorHex = "",
    // ALL active objectives whose token matches this event. An event's token (e.g.
    // DEATH) can be tracked by several objectives at once; the objective-framed VOD
    // viewer lights an event up when the FOCUSED objective is in this list — so a
    // shared token (DEATH, SPELL_FLASH) shows for every objective that tracks it, not
    // just the first-wins winner. Empty = untied. Additive; ObjectiveId still set.
    IReadOnlyList<long>? ObjectiveIds = null,
    string EncounterClassification = "",
    int? EncounterEndSeconds = null,
    string EncounterNote = "",
    bool ReviewedEncounter = false,
    // v3.8: the fight this TEAMFIGHT pin stands for (numbers, window, roster). Set only
    // on TEAMFIGHT entries; a synthetic own-event cluster carries Stored = false.
    VodTeamfightDto? Teamfight = null,
    // v3.11: the corrections ledger's view of this marker. EventKey is the row's stable
    // identity (game_events.event_key; "" when unstamped or synthetic) the fix panel
    // addresses corrections by. Corrected = an applicable retype/retime/attr/confirm is
    // attached; AddedByUser = the row is an op add; Removed = a ghost entry rebuilt from
    // an active op remove (Id = 0, the row itself is gone); Confirmed = the user marked
    // it correct. CorrectionState/Id/Op describe the attached ledger row when any.
    string EventKey = "",
    bool Corrected = false,
    bool AddedByUser = false,
    bool Removed = false,
    bool Confirmed = false,
    string CorrectionState = "",
    string CorrectionId = "",
    string CorrectionOp = "");

/// <summary>
/// An evidence-inbox moment (auto-detected timeline region OR a saved clip) for
/// the 'Moments to Review' panel. Mirrors the EvidenceItemRecord fields the
/// WinUI sidebar surfaces for triage (polarity, status, objective tag, note).
/// </summary>
public sealed record VodEvidenceDto(
    long Id,
    string SourceKind,       // "timeline_region" (auto) | "clip"
    long? SourceId,
    int? StartTimeSeconds,
    int? EndTimeSeconds,
    string TimeLabel,        // start time formatted, or "" if none
    string Title,
    string Note,
    long? ObjectiveId,
    string ObjectiveTitle,
    string Polarity,         // "good" | "neutral" | "bad"
    string PolarityColorHex,
    string Status,           // "needs_review" | "evidence" | "highlight" | "dismissed"
    bool HasClip,            // true for saved-clip rows (SourceKind == clip)
    // For saved-clip rows: the underlying bookmark id (= SourceId) the Share button
    // targets, and the public share link once uploaded ("" until shared). Both ""/0
    // on auto (non-clip) rows.
    long ShareBookmarkId = 0,
    string ShareUrl = "",
    // P-027: the custom prompt this moment answers (evidence_items.prompt_id), null
    // when untagged. Lets the VOD row's picker re-select the saved prompt and the
    // '↳ prompt' badge render after a reload.
    long? PromptId = null);

/// <summary>
/// The fight behind a TEAMFIGHT timeline pin (v3.8 teamfight numbers). Start/End are the
/// fight's own window (the band); Numbers/Verdict are the count at the player's commitment
/// instant ("3v2" / "up" | "even" | "down"); Became is the whole-fight count. Allies/
/// Enemies are the display names of the champions counted in Numbers. Stored is false
/// for a synthetic own-event cluster (no post-game row yet), in which case the numbers
/// fields are empty.
/// </summary>
public sealed record VodTeamfightDto(
    int StartSeconds,
    int EndSeconds,
    string Numbers,
    string Verdict,
    string Self,
    string Became,
    int? EntrySeconds,
    int Kills,
    int KillsFor,
    int KillsAgainst,
    string Outcome,
    IReadOnlyList<string> Allies,
    IReadOnlyList<string> Enemies,
    bool Stored);

// ─────────────────────────────────────────────────────────────────────────────
// v3.11 event corrections (the fix panel's catalog + this game's ledger)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One allowed value of a choice attribute ("short" / "Short trade").</summary>
public sealed record VodEventAttrOptionDto(string Value, string Label);

/// <summary>One editable attribute of a correctable type. Input is "bool" | "choice".</summary>
public sealed record VodEventAttrDto(
    string Key,
    string Label,
    string Input,
    IReadOnlyList<VodEventAttrOptionDto> Options);

/// <summary>One correctable event type. Kind is "point" | "span" (spans carry an end time).</summary>
public sealed record VodEventTypeDto(
    string Type,
    string Label,
    string Kind,
    string ColorHex,
    IReadOnlyList<VodEventAttrDto> Attrs);

/// <summary>
/// One event_corrections row as the VOD panel's corrections list and GET /api/corrections
/// show it. EventType / GameTimeSeconds / TimeLabel are the EFFECTIVE values after the
/// cumulative patch; SubjectType / SubjectTimeSeconds are the row as detected.
/// </summary>
public sealed record VodCorrectionDto(
    long Id,
    string CorrectionId,
    string Op,               // retype|retime|attr|remove|add|confirm
    string State,            // active|absorbed|orphaned|superseded|reverted
    string StateLabel,       // "applied" for active, else the state
    string SubjectKey,
    string SubjectType,
    int SubjectTimeSeconds,
    string EventType,        // effective type
    int GameTimeSeconds,     // effective anchor
    string TimeLabel,        // effective, "12:41"
    string Summary,          // "Death moved 13:32 to 13:35"
    string Reason,
    long? AppliedEventId,
    string ApplyError,
    long CreatedAt,
    bool CanRevert);         // state in (active, absorbed, orphaned)

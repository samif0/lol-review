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
    IReadOnlyList<VodEvidenceDto> SavedClips);

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
    long? ObjectiveId = null,
    string ObjectiveTitle = "",
    string ObjectiveColorHex = "");

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
    string ShareUrl = "");

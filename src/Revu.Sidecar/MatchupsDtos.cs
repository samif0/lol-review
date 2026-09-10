#nullable enable

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// Response DTOs for GET /api/matchups (the Matchups page — the matchup journal).
//
// Same conventions as Dtos.cs: PascalCase here, camelCase on the wire (the
// serializer in Program.cs uses JsonNamingPolicy.CamelCase); the shape matches
// desktop/ui/sample-matchups.json verbatim. Cards come grouped by lane (fixed
// order top / jungle / mid / bot / support, empty lanes omitted) then by
// matchup key, newest first at every level. `lastGame` is the read-side preview
// of the "New card from last game" action: what the POST /api/matchup/
// from-last-game write would create, or why it can't.
//
// Null vs empty:
//   - `lanes` is always present (possibly empty), never null.
//   - a card's `gameId` is null when it is not linked to a game; `gameLabel` is
//     "" then (and also when the linked game is no longer on record).
//   - `lastGame.existingCardId` is null unless a card already links to that game.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level matchup-journal snapshot returned by GET /api/matchups.</summary>
public sealed record MatchupsDto(
    string GeneratedAt,
    int TotalCount,
    bool IsEmpty,
    string EmptyMessage,
    IReadOnlyList<MatchupLaneDto> Lanes,
    LastGamePrefillDto LastGame);

/// <summary>One lane bucket: its label, how many champions a side holds, and its matchup groups.</summary>
public sealed record MatchupLaneDto(
    string Lane,
    string LaneLabel,
    // 1 for top / mid (1v1), 2 for jungle / bot / support (2v2).
    int ChampSlots,
    int CardCount,
    IReadOnlyList<MatchupGroupDto> Groups);

/// <summary>One matchup (lane + champion pairing) and its cards, newest first.</summary>
public sealed record MatchupGroupDto(
    // Stable grouping identity: "bot|kaisa+nautilus|tristana+renataglasc".
    string Key,
    // "Kai'Sa + Nautilus vs Tristana + Renata Glasc".
    string Title,
    IReadOnlyList<string> AllyChamps,
    IReadOnlyList<string> EnemyChamps,
    int CardCount,
    // created_at of the newest card in the group (groups sort by this, newest first).
    long LatestCreatedAt,
    IReadOnlyList<MatchupCardDto> Cards);

/// <summary>One journal card. Mirrors a row of the matchups table plus display strings.</summary>
public sealed record MatchupCardDto(
    long Id,
    string Lane,
    string LaneLabel,
    IReadOnlyList<string> AllyChamps,
    IReadOnlyList<string> EnemyChamps,
    string MatchupKey,
    string MatchupTitle,
    string Prior,
    string Observed,
    bool HasPrior,
    bool HasObserved,
    // The game the card was pre-filled from, or null for a hand-written card.
    long? GameId,
    bool HasGame,
    // "Sep 5, 2026 · Win", or "" when unlinked / the game is gone.
    string GameLabel,
    // Unix seconds (UTC), as stored.
    long CreatedAt,
    // Local "MMM d, yyyy" for the row.
    string CreatedAtText,
    // Local "yyyy-MM-dd" (the export's date line).
    string DateText);

/// <summary>
/// Read-side preview of "New card from last game": the lane + champions the
/// write would pre-fill from the most recent game's participants, or the
/// reason it can't (no games yet / lane unresolvable). "Most recent" is the
/// newest ranked / manual, non-hidden game — the scope every games list uses —
/// never a casual game.
///
/// <para>v3.9.2: <c>available</c> means the lane and the player's OWN side are
/// known. A game recovered from the client's match history can lack the
/// opponents; then <c>enemyKnown</c> is false, <c>enemyChamps</c> is empty and
/// <c>hint</c> says what the click will do (look them up from Riot when signed
/// in, otherwise open the form for the player to add them). When a card already
/// links to the game (<c>existingCardId</c>), the fields describe that card.</para>
/// </summary>
public sealed record LastGamePrefillDto(
    bool Available,
    // 0 when there is no game at all.
    long GameId,
    string Lane,
    string LaneLabel,
    IReadOnlyList<string> AllyChamps,
    IReadOnlyList<string> EnemyChamps,
    // False when the opponents weren't recorded for this game (see Hint).
    bool EnemyKnown,
    string MatchupTitle,
    // "Sep 8, 2026 · Win" — also set when unavailable but a game exists, so the
    // reason line can name the game it is talking about.
    string GameLabel,
    // Non-empty when the click opens the form instead of creating the card
    // outright: what it will do (look the opponents up / have you add them /
    // have you check a lane guessed from your primary role).
    string Hint,
    // Set when a card already links to that game — the page opens it instead of
    // creating a second one.
    long? ExistingCardId,
    // "" when available; otherwise the sentence the disabled button explains itself with.
    string UnavailableReason);

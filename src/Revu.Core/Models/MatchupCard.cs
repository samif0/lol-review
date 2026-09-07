#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// v3.9 (schema v15): one matchup journal card. <see cref="Lane"/> is one of
/// <see cref="Services.MatchupLanes.All"/>; the champion lists hold 1–2 display
/// names each, in slot order (top / mid: the laner; jungle: jungler then mid;
/// bot / support: ADC then support). <see cref="Prior"/> is what the player
/// expects before queuing, <see cref="Observed"/> what actually happened.
/// <see cref="GameId"/> links the card to the game it was pre-filled from, or
/// null for a hand-written card. Never scored.
/// </summary>
public sealed record MatchupCard(
    long Id,
    string Lane,
    IReadOnlyList<string> AllyChamps,
    IReadOnlyList<string> EnemyChamps,
    string Prior,
    string Observed,
    long? GameId,
    long CreatedAt);

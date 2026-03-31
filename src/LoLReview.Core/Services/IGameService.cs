#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

/// <summary>
/// Orchestrates game-end processing: saving stats, session logging,
/// event computation, and VOD matching.
/// Ported from Python main.py App._on_game_end.
/// </summary>
public interface IGameService
{
    /// <summary>
    /// Process an end-of-game event from the LCU monitor.
    /// Returns the game id if saved, or null if skipped (casual/remake).
    /// </summary>
    Task<long?> ProcessGameEndAsync(
        ProcessGameEndRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save a manually entered game with minimal fields.
    /// Returns the generated game id.
    /// </summary>
    Task<long> ProcessManualEntryAsync(
        string championName,
        bool win,
        int kills = 0,
        int deaths = 0,
        int assists = 0,
        string gameMode = "Manual Entry",
        string notes = "",
        string mistakes = "",
        string wentWell = "",
        string focusNext = "",
        List<string>? tags = null,
        int mentalRating = 5);

    /// <summary>Save a post-game review for a given game.</summary>
    Task SaveReviewAsync(long gameId, GameReview review);

    /// <summary>Get a game by id for the review UI.</summary>
    Task<GameStats?> GetGameForReviewAsync(long gameId);
}

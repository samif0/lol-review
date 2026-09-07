#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// v3.9 (schema v15): CRUD for the matchups table — the matchup journal.
/// Validation lives in the repository (lane vocabulary, 1..slots champions per
/// side, note length) and surfaces as <see cref="ArgumentException"/> carrying
/// the sentence the UI shows — the same contract
/// <see cref="ReviewedEncountersRepository"/> uses, so the sidecar's 400 and
/// the tests pin one message.
/// </summary>
public interface IMatchupsRepository
{
    /// <summary>
    /// Insert a card. Champion names are canonicalized and notes trimmed;
    /// <paramref name="createdAt"/> defaults to now. Returns the new row id.
    /// </summary>
    Task<long> CreateAsync(
        string? lane,
        IEnumerable<string?>? allyChamps,
        IEnumerable<string?>? enemyChamps,
        string? prior = null,
        string? observed = null,
        long? gameId = null,
        long? createdAt = null);

    Task<MatchupCard?> GetAsync(long id);

    /// <summary>Every card, newest first.</summary>
    Task<IReadOnlyList<MatchupCard>> GetAllAsync();

    /// <summary>The newest card linked to <paramref name="gameId"/>, or null.</summary>
    Task<MatchupCard?> GetForGameAsync(long gameId);

    /// <summary>Replace lane, champions and both notes. False when the id does not exist.</summary>
    Task<bool> UpdateAsync(
        long id,
        string? lane,
        IEnumerable<string?>? allyChamps,
        IEnumerable<string?>? enemyChamps,
        string? prior,
        string? observed);

    /// <summary>Inline edit: a null note is left unchanged. False when the id does not exist.</summary>
    Task<bool> UpdateNotesAsync(long id, string? prior, string? observed);

    /// <summary>Hard delete. False when the id does not exist.</summary>
    Task<bool> DeleteAsync(long id);
}

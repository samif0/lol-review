#nullable enable

namespace LoLReview.Core.Data.Repositories;

/// <summary>Aggregate stats on matchup note helpfulness.</summary>
public sealed record HelpfulnessStats(
    int Total,
    int HelpfulCount,
    int UnhelpfulCount,
    int UnratedCount);

/// <summary>CRUD for matchup_notes table.</summary>
public interface IMatchupNotesRepository
{
    Task<long> CreateAsync(string champion, string enemy, string note = "",
        int? helpful = null, long? gameId = null);

    /// <summary>Get the matchup note associated with a specific game, if one exists.</summary>
    Task<MatchupNoteRecord?> GetForGameAsync(long gameId);

    /// <summary>Get notes for an exact champion vs enemy matchup.</summary>
    Task<IReadOnlyList<MatchupNoteRecord>> GetForMatchupAsync(string champion, string enemy);

    Task<IReadOnlyList<MatchupNoteRecord>> GetAllAsync();

    /// <summary>Create or update the note attached to a specific game.</summary>
    Task<long?> UpsertForGameAsync(long gameId, string champion, string enemy, string note);

    Task DeleteForGameAsync(long gameId);

    Task UpdateHelpfulAsync(long noteId, int helpful);

    /// <summary>Get aggregate stats on matchup note helpfulness.</summary>
    Task<HelpfulnessStats> GetHelpfulnessStatsAsync();
}

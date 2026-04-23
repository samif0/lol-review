#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>Frequency stats for a concept tag across games.</summary>
public sealed record TagFrequency(
    string Name,
    string Polarity,
    string Color,
    int Count,
    double GamePercent);

/// <summary>CRUD for concept_tags and game_concept_tags tables.</summary>
public interface IConceptTagRepository
{
    Task<IReadOnlyList<ConceptTagRecord>> GetAllAsync();

    /// <summary>Create a concept tag. Auto-assigns a color if not specified.</summary>
    Task<long> CreateAsync(string name, string polarity = "neutral", string color = "");

    Task<IReadOnlyList<long>> GetIdsForGameAsync(long gameId);

    /// <summary>Replace the concept tags for a game.</summary>
    Task SetForGameAsync(long gameId, IReadOnlyList<long> tagIds);

    /// <summary>Get concept tag usage frequency across all games.</summary>
    Task<IReadOnlyList<TagFrequency>> GetTagFrequencyAsync(int limit = 20);
}

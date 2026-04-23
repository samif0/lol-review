#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD + computation for derived event definitions and instances.</summary>
public interface IDerivedEventsRepository
{
    Task<long> CreateAsync(string name, IReadOnlyList<string> sourceTypes, int minCount,
        int windowSeconds, string color = "#ff6b6b");

    Task<IReadOnlyList<DerivedEventDefinitionRecord>> GetAllDefinitionsAsync();

    /// <summary>Delete a non-default definition and its instances.</summary>
    Task DeleteDefinitionAsync(long definitionId);

    /// <summary>
    /// Compute derived event instances from raw game events using a sliding window
    /// clustering algorithm. For each definition, filter events to matching source_types,
    /// sort by time, and use greedy non-overlapping windows.
    /// </summary>
    IReadOnlyList<DerivedEventInstanceRecord> ComputeInstances(
        long gameId,
        IReadOnlyList<Revu.Core.Models.GameEvent> events,
        IReadOnlyList<DerivedEventDefinitionRecord> definitions);

    /// <summary>Save computed instances for a game, replacing existing ones.</summary>
    Task SaveInstancesAsync(long gameId, IReadOnlyList<DerivedEventInstanceRecord> instances);

    /// <summary>Get all derived event instances for a game, with definition info.</summary>
    Task<IReadOnlyList<DerivedEventInstanceRecord>> GetInstancesAsync(long gameId);
}

#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD for the persistent_notes table (single-row persistent notes).</summary>
public interface INotesRepository
{
    /// <summary>Get the persistent notes content (empty string if none).</summary>
    Task<string> GetAsync();

    /// <summary>Save / overwrite the persistent notes content.</summary>
    Task SaveAsync(string content);
}

#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Player profiling and smart objective suggestion generation.
/// Ported from Python analysis/profile.py and analysis/suggestions.py.
/// </summary>
public interface IAnalysisService
{
    /// <summary>
    /// Build a comprehensive player profile from all available repository data.
    /// Each section is independently wrapped in try/catch so a failure in one area
    /// does not prevent the rest of the profile from being generated.
    /// </summary>
    Task<PlayerProfile> GenerateProfileAsync();

    /// <summary>
    /// Build a player profile restricted to games matching <paramref name="filter"/>.
    /// Falls back to <see cref="GenerateProfileAsync()"/> when the filter is
    /// <see cref="AnalyticsFilter.None"/> / empty.
    ///
    /// Aggregates (champion stats, matchups, mental brackets, etc.) are
    /// recomputed from the filtered game set in-memory — this is the same
    /// dataset users see on the page, just narrowed to match the filter.
    /// </summary>
    Task<PlayerProfile> GenerateProfileAsync(AnalyticsFilter filter);

    /// <summary>
    /// Analyze the profile and return top objective suggestions sorted by confidence.
    /// Uses 7 deterministic rules: vision, CS, deaths, mental gap, negative tags,
    /// losing matchups, and spotted problems.
    /// </summary>
    List<ObjectiveSuggestion> GenerateSuggestions(PlayerProfile profile, int limit = 3);
}

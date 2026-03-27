#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

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
    /// Analyze the profile and return top objective suggestions sorted by confidence.
    /// Uses 7 deterministic rules: vision, CS, deaths, mental gap, negative tags,
    /// losing matchups, and spotted problems.
    /// </summary>
    List<ObjectiveSuggestion> GenerateSuggestions(PlayerProfile profile, int limit = 3);
}
